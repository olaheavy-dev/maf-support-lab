namespace Support.Api.Modules.Triage.Domain;

/// <summary>
/// The deterministic part of routing. The model classifies; these rules decide. A target the requested action does
/// not belong to is treated as ambiguous rather than trusted, and actions about a specific order need an order id.
/// </summary>
public static class RoutingRules
{
    /// <summary>The one team each action belongs to. "other" belongs to none and is never routed.</summary>
    public static RouteTarget? TeamFor(RequestedAction action) => action switch
    {
        RequestedAction.OrderStatus or RequestedAction.CancelOrder => RouteTarget.Orders,
        RequestedAction.RefundOrder or RequestedAction.Invoice or RequestedAction.PaymentHistory => RouteTarget.Billing,
        RequestedAction.Troubleshooting or RequestedAction.CreateTicket => RouteTarget.Technical,
        _ => null,
    };

    /// <summary>Actions that are about one specific order and cannot proceed without its id.</summary>
    public static bool NeedsOrderId(RequestedAction action) =>
        action is RequestedAction.OrderStatus or RequestedAction.CancelOrder or RequestedAction.RefundOrder;

    /// <summary>Routes a valid decision, or reports it as ambiguous when the action is "other" or disagrees with the target.</summary>
    public static TurnRoute Route(TriageDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return TeamFor(decision.RequestedAction) == decision.Target
            ? TurnRoute.Routed(decision.Target)
            : TurnRoute.Unrouted(UnroutedReason.AmbiguousRequest);
    }
}
