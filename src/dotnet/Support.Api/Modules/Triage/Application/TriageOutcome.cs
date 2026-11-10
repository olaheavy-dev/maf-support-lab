using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Triage.Application;

/// <summary>
/// What the triage step hands to the router: the customer's original message kept beside the validated fields
/// (the contract's routed-request), and the deterministic routing result. The principal is not in here; it stays in
/// trusted server context (IPrincipalSource) and tools read it from there.
/// </summary>
public sealed record TriageOutcome(string OriginalMessage, TriageDecision? Triage, TurnRoute Route)
{
    /// <summary>A routed request to change something (cancel, refund, ticket): not possible before L10's approvals.</summary>
    public bool IsWriteRequest => Route.IsRouted && Triage is { } decision && RoutingRules.IsWrite(decision.RequestedAction);

    /// <summary>Routed to orders for an action about one order, but no order id was given.</summary>
    public bool IsMissingOrderId =>
        Route.Target == RouteTarget.Orders && Triage is { OrderId: null } decision && RoutingRules.NeedsOrderId(decision.RequestedAction);
}
