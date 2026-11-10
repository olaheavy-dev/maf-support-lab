using System.Diagnostics;
using System.Globalization;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Triage.Application;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Triage.Infrastructure.Routing;

/// <summary>
/// Runs the triage agent, validates its output against the full contract schema, and applies the deterministic
/// routing rules. The model only classifies; whether and where a request goes is decided here, in code.
/// </summary>
internal sealed partial class TriageExecutor(SupportAgents agents, TriageMetrics metrics, ILogger logger)
    : Executor<RoutingRequest, TriageOutcome>("triage")
{
    public override async ValueTask<TriageOutcome> HandleAsync(RoutingRequest request, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Triage is stateless (no session): it classifies this one message. A follow-up such as "and the invoice for it?"
        // gets the previous route as a short, code-written hint; the message itself is still what gets classified.
        List<ChatMessage> input = [];
        if (request.Context.LastTarget is { } lastTarget)
        {
            input.Add(ConversationHistory.ContextNote($"The previous request in this conversation was routed to {Names.Of(lastTarget)}."));
        }

        input.Add(new ChatMessage(ChatRole.User, request.Message));
        var response = await agents.Get(TriageAgentFactory.AgentName).RunAsync(input, cancellationToken: cancellationToken).ConfigureAwait(false);
        var parsed = TriageDecisionParser.Parse(response.Text);

        var outcome = (parsed.IsSuccess
            ? new TriageOutcome(request.Message, parsed.Value, RoutingRules.Route(parsed.Value))
            : new TriageOutcome(request.Message, null, TurnRoute.Unrouted(UnroutedReason.InvalidModelOutput)))
            with
        { ConversationId = request.ConversationId, TurnId = request.TurnId, Context = request.Context };

        Describe(outcome, parsed.IsSuccess ? null : parsed.Error.Description);
        return outcome;
    }

    /// <summary>Records the decision on the executor span, as a metric, and as a log. No message text or summary: those are customer content.</summary>
    private void Describe(TriageOutcome outcome, string? invalidRule)
    {
        var target = outcome.Route.Target is { } routed ? Names.Of(routed) : "none";
        var reason = outcome.Route.UnroutedReason is { } unrouted ? Names.Of(unrouted) : "none";
        var result = outcome.Route.IsRouted ? "routed" : "unrouted";

        var span = Activity.Current;
        span?.SetTag("support.triage.outcome", result);
        span?.SetTag("support.triage.target", target);
        span?.SetTag("support.triage.unrouted_reason", reason);
        if (outcome.Triage is { } decision)
        {
            span?.SetTag("support.triage.requested_action", Names.Of(decision.RequestedAction));
            span?.SetTag("support.triage.order_id_present", decision.OrderId is not null);
            span?.SetTag("support.triage.order_id_from_context", decision.OrderId is null && outcome.EffectiveOrderId is not null);
            // Recorded to study routing quality later. Never used to decide anything: it is not a calibrated probability.
            span?.SetTag("support.triage.model_confidence", decision.Confidence);
        }

        metrics.Record(result, target, reason);
        if (outcome.Route.IsRouted)
        {
            Log.Routed(logger, target, outcome.Triage is { } d ? Names.Of(d.RequestedAction) : "none");
        }
        else
        {
            Log.Unrouted(logger, reason, invalidRule ?? "action and target disagree, or the action is other");
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Triage routed the request to {Target} for {RequestedAction}")]
        public static partial void Routed(ILogger logger, string target, string requestedAction);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Triage left the request unrouted ({Reason}): {Rule}")]
        public static partial void Unrouted(ILogger logger, string reason, string rule);
    }
}
