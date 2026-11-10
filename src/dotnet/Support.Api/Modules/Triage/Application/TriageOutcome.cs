using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Triage.Application;

/// <summary>
/// What the triage step hands to the router: the customer's original message kept beside the validated fields
/// (the contract's routed-request), and the deterministic routing result. The principal is not in here; it stays in
/// trusted server context (IPrincipalSource) and tools read it from there.
/// </summary>
public sealed record TriageOutcome(string OriginalMessage, TriageDecision? Triage, TurnRoute Route)
{
    /// <summary>The conversation whose sessions the specialist should use (null: stateless).</summary>
    public string? ConversationId { get; init; }

    /// <summary>The turn being run (null for a stateless DevUI chat).</summary>
    public string? TurnId { get; init; }

    /// <summary>Verified facts from earlier turns in this conversation.</summary>
    public ConversationContext Context { get; init; } = ConversationContext.Empty;

    /// <summary>
    /// The order this request is about: the one the customer named, or, for a follow-up that names none, the order
    /// verified earlier in this conversation. Never an unverified id from an earlier turn.
    /// </summary>
    public string? EffectiveOrderId => Triage?.OrderId ?? Context.VerifiedOrderId;

    /// <summary>Routed to orders for an action about one order, but no order id was given.</summary>
    public bool IsMissingOrderId =>
        Route.Target == RouteTarget.Orders && EffectiveOrderId is null && Triage is { } decision && RoutingRules.NeedsOrderId(decision.RequestedAction);
}
