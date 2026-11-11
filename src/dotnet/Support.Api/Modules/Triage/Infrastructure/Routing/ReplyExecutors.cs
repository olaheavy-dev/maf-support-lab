using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Support.Api.Common.Application.Streaming;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Triage.Application;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Triage.Infrastructure.Routing;

/// <summary>
/// Runs the routed specialist agent (orders, billing, or technical) on the customer's original message. Each agent has
/// only its own read tools; the principal reaches those tools through trusted DI, never through this payload.
/// </summary>
[SendsMessage(typeof(SupportReply))]
internal sealed partial class SpecialistExecutor(SupportAgents agents, ConversationSessions sessions, ApprovalGate gate, RouteTarget target)
    : Executor<TriageOutcome>($"{Names.Of(target)}-specialist")
{
    public override async ValueTask HandleAsync(TriageOutcome request, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var name = Names.Of(target);
        var agent = agents.Get(name);

        // The conversation's own native session for this specialist (L09): its bounded history comes back each turn.
        AgentSession? session = request.ConversationId is { } conversationId
            ? await sessions.GetOrCreateAsync(conversationId, name, agent, cancellationToken).ConfigureAwait(false)
            : null;

        // A follow-up that names no order gets the order a tool verified earlier, as a trusted note for this turn only.
        // The note comes right after the customer's message: Anthropic accepts a mid-conversation system message only
        // after a user message (a note placed after the previous assistant reply was rejected with HTTP 400 in L09).
        List<ChatMessage> input = [new ChatMessage(ChatRole.User, request.OriginalMessage)];
        if (request.Triage?.OrderId is null && request.Context.VerifiedOrderId is { } verified)
        {
            input.Add(ConversationHistory.ContextNote(
                $"Verified context: earlier in this conversation the customer's order {verified} was found. If they refer to \"it\" or \"that order\", they mean {verified}."));
        }
        using var activity = TurnActivity.Begin();
        await context.AddEventAsync(new SupportProgressEvent(new TurnProgress(TurnStage.Specialist, Names.Of(target))), cancellationToken).ConfigureAwait(false);

        // Streamed (L12): the model's text goes out as it arrives, and each tool call as a progress step. The collected
        // updates are the same response a non-streaming run returns, so everything below is unchanged.
        List<AgentResponseUpdate> updates = [];
        await foreach (var update in agent.RunStreamingAsync(input, session, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
            await ForwardAsync(update, context.AddEventAsync, cancellationToken).ConfigureAwait(false);
        }

        var response = updates.ToAgentResponse();

        // A write tool pauses natively for approval; the gate binds it to an operation or declines it (L10).
        var gated = await gate.AfterRunAsync(agent, session, response, request, target, cancellationToken).ConfigureAwait(false);
        if (gated.Proposed is not null)
        {
            await context.AddEventAsync(new SupportProgressEvent(new TurnProgress(TurnStage.AwaitingApproval)), cancellationToken).ConfigureAwait(false);
        }

        if (session is not null)
        {
            await sessions.SaveAsync(request.ConversationId!, name, agent, session, cancellationToken).ConfigureAwait(false);
        }

        var reply = gated switch
        {
            { Proposed: { } operation } => new SupportReply(request.Route, ReplyKind.ApprovalRequest, $"I can do this for you: {operation.Summary} Please approve or reject.", activity.Activity.VerifiedOrderId)
            {
                PendingOperationId = operation.OperationId,
                OperationIds = [operation.OperationId],
            },
            { DeclinedCode: "not_eligible" } => new SupportReply(request.Route, ReplyKind.NotEligible, ReplyText(gated.Response.Text), activity.Activity.VerifiedOrderId),
            { DeclinedCode: "not_found" } => new SupportReply(request.Route, ReplyKind.NotFound, ReplyText(gated.Response.Text), activity.Activity.VerifiedOrderId),
            { DeclinedCode: not null } => new SupportReply(request.Route, ReplyKind.Clarification, ReplyText(gated.Response.Text), activity.Activity.VerifiedOrderId),
            _ => new SupportReply(request.Route, ReplyKindFrom(activity.Activity), ReplyText(gated.Response.Text), activity.Activity.VerifiedOrderId),
        };
        await context.SendMessageAsync(reply, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Turns one streamed update into stream events: a tool call becomes a progress step, text becomes a delta.</summary>
    internal static async ValueTask ForwardAsync(AgentResponseUpdate update, Func<WorkflowEvent, CancellationToken, ValueTask> raise, CancellationToken cancellationToken)
    {
        // A write tool the model asks for arrives wrapped in an approval request (it has not run yet). It is still a tool
        // call, as in Python's stream, so it is reported too (found by the L29 evaluation datasets).
        var calls = update.Contents.OfType<FunctionCallContent>()
            .Concat(update.Contents.OfType<ToolApprovalRequestContent>().Select(request => request.ToolCall).OfType<FunctionCallContent>());
        foreach (var call in calls)
        {
            await raise(new SupportProgressEvent(new TurnProgress(TurnStage.ToolCall, Tool: call.Name)), cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(update.Text))
        {
            await raise(new SupportReplyDeltaEvent(update.Text), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The reply kind comes from what the tools recorded, not from the model's wording: any record found is an answer;
    /// otherwise a missing record is not_found; a search that matched nothing is an unresolved issue (escalation offer);
    /// no lookup at all means the specialist asked for more information.
    /// </summary>
    internal static ReplyKind ReplyKindFrom(TurnActivity activity)
    {
        var outcomes = activity.Outcomes;
        return outcomes.Contains(ToolOutcome.Found) ? ReplyKind.Answer
            : outcomes.Contains(ToolOutcome.NotFound) ? ReplyKind.NotFound
            : outcomes.Contains(ToolOutcome.NoMatch) ? ReplyKind.EscalationOffer
            : ReplyKind.Clarification;
    }

    internal static string ReplyText(string text) =>
        string.IsNullOrWhiteSpace(text) ? "Sorry, I couldn't produce an answer. Please try again." : text.Trim();
}

/// <summary>An order question without an order id: ask for it, deterministically, without spending a model call.</summary>
[SendsMessage(typeof(SupportReply))]
internal sealed partial class AskForOrderIdExecutor() : Executor<TriageOutcome>("ask-for-order-id")
{
    public const string Text = "Which order do you mean? Please share the order id, for example ORD-1234.";

    public override ValueTask HandleAsync(TriageOutcome request, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return context.SendMessageAsync(new SupportReply(request.Route, ReplyKind.Clarification, Text), cancellationToken: cancellationToken);
    }
}

/// <summary>The fallback for invalid or ambiguous triage: never guess a route; ask the customer instead.</summary>
[SendsMessage(typeof(SupportReply))]
internal sealed partial class UnroutedExecutor() : Executor<TriageOutcome>("unrouted")
{
    public const string Text = "I'm not sure what you need. Is this about an order, billing, or a technical problem?";

    public override ValueTask HandleAsync(TriageOutcome request, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        // The default branch also catches anything no case matched, so make sure the route says "unrouted".
        var route = request.Route.IsRouted ? TurnRoute.Unrouted(UnroutedReason.AmbiguousRequest) : request.Route;
        return context.SendMessageAsync(new SupportReply(route, ReplyKind.Clarification, Text), cancellationToken: cancellationToken);
    }
}

/// <summary>The API's workflow output: the finished reply, exactly once per turn.</summary>
[YieldsOutput(typeof(SupportReply))]
internal sealed partial class ReplyOutputExecutor() : Executor<SupportReply>("reply")
{
    public override ValueTask HandleAsync(SupportReply reply, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.YieldOutputAsync(reply, cancellationToken);
    }
}

/// <summary>
/// DevUI only: the same reply as an agent response update, with the routing result on a first line so the chat shows
/// where the request went (for example "[route: billing | reply: answer]"). A workflow hosted as an agent always
/// forwards AgentResponseUpdateEvent to the chat; plain workflow outputs are forwarded only if the host opts in.
/// </summary>
[YieldsOutput(typeof(AgentResponseUpdate))]
internal sealed partial class ChatReplyExecutor() : Executor<SupportReply>("reply")
{
    public override ValueTask HandleAsync(SupportReply reply, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(context);
        var route = reply.Route.Target is { } target ? Names.Of(target) : $"unrouted ({Names.Of(reply.Route.UnroutedReason!.Value)})";
        var text = $"[route: {route} | reply: {Names.Of(reply.Kind)}]\n{reply.Text}";
        return context.AddEventAsync(new AgentResponseUpdateEvent(Id, new AgentResponseUpdate(ChatRole.Assistant, text)), cancellationToken);
    }
}

/// <summary>
/// DevUI only: speaks MAF's chat protocol (messages plus a turn token) at the workflow's entrance and forwards the
/// customer's latest text to triage, so a DevUI chat runs exactly the executors an API turn runs.
/// </summary>
internal sealed class ChatInputExecutor() : ChatProtocolExecutor("chat-input", new ChatProtocolExecutorOptions { AutoSendTurnToken = false })
{
    /// <summary>Declares the request it forwards to triage. (The SendsMessage attribute is not applied to ChatProtocolExecutor subclasses.)</summary>
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder).SendsMessage<RoutingRequest>();

    protected override ValueTask TakeTurnAsync(List<ChatMessage> messages, IWorkflowContext context, bool? emitEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(context);
        var text = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;
        // DevUI workflow chats are stateless: no conversation id, no verified context.
        return context.SendMessageAsync(new RoutingRequest(null, text, ConversationContext.Empty), cancellationToken: cancellationToken);
    }
}
