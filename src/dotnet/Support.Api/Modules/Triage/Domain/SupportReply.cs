namespace Support.Api.Modules.Triage.Domain;

/// <summary>What kind of reply the customer got. Decided by code (tool facts, routing), never by the model's wording.</summary>
public enum ReplyKind
{
    Answer,
    NotFound,
    Clarification,

    /// <summary>Unresolved (no documented fix): offer a human instead of guessing.</summary>
    EscalationOffer,

    /// <summary>A write was requested but the business rules do not allow it; nothing was proposed.</summary>
    NotEligible,

    /// <summary>A bound write is waiting for the customer's decision.</summary>
    ApprovalRequest,

    /// <summary>The outcome of the customer's decision: committed, failed, or rejected.</summary>
    ApprovalResult,
}

/// <summary>
/// The outcome of routing one message: where it went, what the customer is told, and the order a tool verified for
/// this customer during the turn (if any), which the conversation may remember for follow-ups.
/// </summary>
public sealed record SupportReply(TurnRoute Route, ReplyKind Kind, string Text, string? VerifiedOrderId = null)
{
    /// <summary>The operation now awaiting the customer's approval, if this turn proposed one.</summary>
    public string? PendingOperationId { get; init; }

    /// <summary>Operations this turn created or changed.</summary>
    public IReadOnlyList<string> OperationIds { get; init; } = [];
}
