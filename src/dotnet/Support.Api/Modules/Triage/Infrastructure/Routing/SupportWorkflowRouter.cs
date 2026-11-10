using Microsoft.Agents.AI.Workflows;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Triage.Application;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Triage.Infrastructure.Routing;

/// <summary>
/// The support workflow (MAF Workflows): triage, then a deterministic switch to exactly one executor.
/// A fresh workflow is built per message, so concurrent turns never share executor state.
/// <code>
/// triage ─┬─ orders, no order id ─▶ ask-for-order-id
///         ├─ orders              ─▶ orders-specialist
///         ├─ billing             ─▶ billing-placeholder
///         ├─ technical           ─▶ technical-placeholder
///         └─ default             ─▶ unrouted (invalid or ambiguous triage)
/// </code>
/// </summary>
internal sealed class SupportWorkflowRouter(SupportAgents agents, TriageMetrics metrics, ILogger<SupportWorkflowRouter> logger) : ISupportRouter
{
    public const string WorkflowName = "support-routing";

    public async Task<SupportReply> RouteAsync(string message, CancellationToken cancellationToken)
    {
        var workflow = Build();
        await using var run = await InProcessExecution.RunAsync(workflow, message, cancellationToken: cancellationToken).ConfigureAwait(false);

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

    private Workflow Build()
    {
        var triage = new TriageExecutor(agents, metrics, logger);
        var askForOrderId = new AskForOrderIdExecutor();
        var orders = new OrdersSpecialistExecutor(agents);
        var billing = new PlaceholderSpecialistExecutor(RouteTarget.Billing);
        var technical = new PlaceholderSpecialistExecutor(RouteTarget.Technical);
        var unrouted = new UnroutedExecutor();

        return new WorkflowBuilder(triage)
            .WithName(WorkflowName)
            .AddSwitch(triage, cases => cases
                .AddCase<TriageOutcome>(outcome => outcome is { IsMissingOrderId: true }, [askForOrderId])
                .AddCase<TriageOutcome>(outcome => outcome?.Route.Target == RouteTarget.Orders, [orders])
                .AddCase<TriageOutcome>(outcome => outcome?.Route.Target == RouteTarget.Billing, [billing])
                .AddCase<TriageOutcome>(outcome => outcome?.Route.Target == RouteTarget.Technical, [technical])
                .WithDefault([unrouted]))
            .WithOutputFrom(askForOrderId, orders, billing, technical, unrouted)
            .WithOpenTelemetry(activitySource: AgentTelemetry.WorkflowActivitySource)
            .Build();
    }
}
