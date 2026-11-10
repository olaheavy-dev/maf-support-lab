namespace Support.Api.Modules.Triage.Domain;

/// <summary>Where a request can go. Matches the contract's route_target (contracts/schemas/common.schema.json).</summary>
public enum RouteTarget
{
    Orders,
    Billing,
    Technical,
}

/// <summary>What the customer is asking for, as classified by triage (contracts/schemas/triage-decision.schema.json).</summary>
public enum RequestedAction
{
    OrderStatus,
    CancelOrder,
    RefundOrder,
    Invoice,
    PaymentHistory,
    Troubleshooting,
    CreateTicket,
    Other,
}

public enum CustomerSentiment
{
    Frustrated,
    Neutral,
    Positive,
}

/// <summary>
/// A triage result that passed validation against the full contract schema. Valid shape is not proof of correctness:
/// <see cref="OrderId"/> is a claim to verify, and <see cref="Confidence"/> is model-generated and uncalibrated, so it is
/// recorded for analysis but never used to decide anything.
/// </summary>
public sealed record TriageDecision(
    RouteTarget Target,
    RequestedAction RequestedAction,
    string? OrderId,
    string Summary,
    CustomerSentiment CustomerSentiment,
    double Confidence);
