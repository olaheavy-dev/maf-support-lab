using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Triage.Application;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Triage.Infrastructure.Routing;

/// <summary>
/// The support workflow (MAF Workflows): triage, then a deterministic switch to exactly one executor (first match wins).
/// A fresh workflow is built per message, so concurrent turns never share executor state.
/// <code>
/// triage ─┬─ orders, no order id               ─▶ ask-for-order-id
///         ├─ orders                            ─▶ orders-specialist
///         ├─ billing                           ─▶ billing-specialist
///         ├─ technical                         ─▶ technical-specialist
///         └─ default                           ─▶ unrouted (invalid or ambiguous triage)
/// </code>
/// </summary>
internal sealed class SupportWorkflowRouter(
    SupportAgents agents,
    ConversationSessions sessions,
    ApprovalGate gate,
    PendingNativeApprovals pending,
    IOperationStore operations,
    TriageMetrics metrics,
    ILogger<SupportWorkflowRouter> logger) : ISupportRouter
{
    public const string WorkflowName = "support-routing";

    public async Task<SupportReply> RouteAsync(RoutingRequest request, CancellationToken cancellationToken)
    {
        var workflow = Build();
        await using var run = await InProcessExecution.RunAsync(workflow, request, cancellationToken: cancellationToken).ConfigureAwait(false);

        SupportReply? reply = null;
        foreach (var workflowEvent in run.NewEvents)
        {
            switch (workflowEvent)
            {
                case ExecutorFailedEvent { Data: Exception error }:
                    throw new InvalidOperationException($"Workflow executor '{((ExecutorFailedEvent)workflowEvent).ExecutorId}' failed.", error);
                case WorkflowErrorEvent { Data: Exception error }:
                    throw new InvalidOperationException("The support workflow failed.", error);
                case WorkflowOutputEvent output when output.Is<SupportReply>(out var produced):
                    reply = produced;
                    break;
            }
        }

        return reply ?? throw new InvalidOperationException("The support workflow finished without a reply.");
    }

    /// <summary>
    /// Resumes the paused specialist with the customer's decision (L10), in the conversation's own session. Approve: the
    /// native gate lets the write tool run, inside an <see cref="ApprovalScope"/> naming exactly this operation. Reject:
    /// the tool never runs. Either way, any new approval request in the resumed run is declined: a decision never chains
    /// into another request (after a rejection, this is the "do not ask again" rule).
    /// </summary>
    public async Task<SupportReply> ResumeAsync(ApprovalResume decided, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decided);
        var operation = operations.Find(decided.OperationId) ?? throw new InvalidOperationException("Unknown operation.");
        var route = TurnRoute.Routed(operation.Specialist);
        var native = pending.Take(operation.OperationId);
        if (native is null)
        {
            // The paused request is gone (it can only be resumed once). Nothing runs.
            return new SupportReply(route, ReplyKind.ApprovalResult, "That request can no longer be resumed. Nothing has been changed.") { OperationIds = [operation.OperationId] };
        }

        var name = Names.Of(operation.Specialist);
        var agent = agents.Get(name);
        var session = await sessions.GetOrCreateAsync(operation.ConversationId, name, agent, cancellationToken).ConfigureAwait(false);
        var approved = operation.Status == OperationStatus.Running;

        using (approved ? ApprovalScope.Begin(operation) : null)
        {
            var response = await agent.RunAsync(
                new ChatMessage(ChatRole.User, [native.CreateResponse(approved, approved ? null : decided.Reason ?? "The customer rejected this request.")]),
                session,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            response = await ApprovalGate.DeclineFollowUpRequestsAsync(agent, session, response, cancellationToken).ConfigureAwait(false);

            var text = operation.Status switch
            {
                OperationStatus.Committed => SpecialistExecutor.ReplyText(response.Text),
                OperationStatus.Failed => $"I couldn't complete that: {operation.Failure?.Detail} Nothing has been changed.",
                OperationStatus.Rejected => $"Okay, I did not go ahead. Nothing has been changed: {operation.Summary}",
                _ => "That request was not carried out. Nothing has been changed.",
            };
            return new SupportReply(route, ReplyKind.ApprovalResult, text) { OperationIds = [operation.OperationId] };
        }
    }

    /// <summary>A fresh workflow for one API turn: string in, one <see cref="SupportReply"/> out.</summary>
    public Workflow Build() => Build(forChat: false);

    /// <summary>
    /// Development only: the same workflow for DevUI. A chat-protocol entrance forwards the text to triage, and the
    /// reply comes out as a chat message. Every executor in between is identical to the API's.
    /// </summary>
    public Workflow BuildForChat() => Build(forChat: true);

    private Workflow Build(bool forChat)
    {
        var triage = new TriageExecutor(agents, metrics, logger);
        var askForOrderId = new AskForOrderIdExecutor();
        var orders = new SpecialistExecutor(agents, sessions, gate, RouteTarget.Orders);
        var billing = new SpecialistExecutor(agents, sessions, gate, RouteTarget.Billing);
        var technical = new SpecialistExecutor(agents, sessions, gate, RouteTarget.Technical);
        var unrouted = new UnroutedExecutor();
        ExecutorBinding reply = forChat ? new ChatReplyExecutor() : new ReplyOutputExecutor();
        ExecutorBinding[] endings = [askForOrderId, orders, billing, technical, unrouted];

        var chatInput = new ChatInputExecutor();
        var builder = new WorkflowBuilder(forChat ? chatInput : triage);
        if (forChat)
        {
            builder.AddEdge(chatInput, triage);
        }

        builder
            .WithName(WorkflowName)
            .AddSwitch(triage, cases => cases
                .AddCase<TriageOutcome>(outcome => outcome is { IsMissingOrderId: true }, [askForOrderId])
                .AddCase<TriageOutcome>(outcome => outcome?.Route.Target == RouteTarget.Orders, [orders])
                .AddCase<TriageOutcome>(outcome => outcome?.Route.Target == RouteTarget.Billing, [billing])
                .AddCase<TriageOutcome>(outcome => outcome?.Route.Target == RouteTarget.Technical, [technical])
                .WithDefault([unrouted]));
        foreach (var ending in endings)
        {
            builder.AddEdge(ending, reply);
        }

        return builder
            .WithOutputFrom(reply)
            .WithOpenTelemetry(activitySource: AgentTelemetry.WorkflowActivitySource)
            .Build();
    }
}
