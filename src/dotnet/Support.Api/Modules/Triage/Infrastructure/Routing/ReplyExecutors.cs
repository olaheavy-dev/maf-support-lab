using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Triage.Application;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Triage.Infrastructure.Routing;

/// <summary>
/// Runs the routed specialist agent (orders, billing, or technical) on the customer's original message. Each agent has
/// only its own read tools; the principal reaches those tools through trusted DI, never through this payload.
/// </summary>
[SendsMessage(typeof(SupportReply))]
internal sealed partial class SpecialistExecutor(SupportAgents agents, RouteTarget target) : Executor<TriageOutcome>($"{Names.Of(target)}-specialist")
{
    public override async ValueTask HandleAsync(TriageOutcome request, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using var activity = TurnActivity.Begin();
        var response = await agents.Get(Names.Of(target)).RunAsync(request.OriginalMessage, cancellationToken: cancellationToken).ConfigureAwait(false);
        var reply = new SupportReply(request.Route, ReplyKindFrom(activity.Activity), ReplyText(response.Text));
        await context.SendMessageAsync(reply, cancellationToken: cancellationToken).ConfigureAwait(false);
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

    private static string ReplyText(string text) =>
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

/// <summary>
/// Cancel, refund, and ticket requests until L10 adds them with approval. Answered here, deterministically, so no
/// agent is ever given the chance to claim it changed something. Nothing is changed and nothing is created.
/// </summary>
[SendsMessage(typeof(SupportReply))]
internal sealed partial class WriteNotAvailableExecutor() : Executor<TriageOutcome>("write-not-available")
{
    public override ValueTask HandleAsync(TriageOutcome request, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var what = request.Triage?.RequestedAction switch
        {
            RequestedAction.CancelOrder => "cancel an order",
            RequestedAction.RefundOrder => "request a refund",
            _ => "open a support ticket",
        };
        var text = $"I can see you'd like to {what}, but I can't make changes in this version yet. Nothing has been changed. A human support agent can help with this.";
        return context.SendMessageAsync(new SupportReply(request.Route, ReplyKind.EscalationOffer, text), cancellationToken: cancellationToken);
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
    /// <summary>Declares the string it forwards to triage. (The SendsMessage attribute is not applied to ChatProtocolExecutor subclasses.)</summary>
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder).SendsMessage<string>();

    protected override ValueTask TakeTurnAsync(List<ChatMessage> messages, IWorkflowContext context, bool? emitEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(context);
        var text = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;
        return context.SendMessageAsync(text, cancellationToken: cancellationToken);
    }
}
