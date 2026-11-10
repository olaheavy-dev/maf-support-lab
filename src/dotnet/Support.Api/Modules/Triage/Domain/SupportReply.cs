namespace Support.Api.Modules.Triage.Domain;

/// <summary>What kind of reply the customer got. Decided by code (tool facts, routing), never by the model's wording.</summary>
public enum ReplyKind
{
    Answer,
    NotFound,
    Clarification,

    /// <summary>Unresolved (no documented fix) or not possible yet (writes before L10): offer a human instead of guessing.</summary>
    EscalationOffer,
}

/// <summary>The outcome of routing one message: where it went and what the customer is told.</summary>
public sealed record SupportReply(TurnRoute Route, ReplyKind Kind, string Text);
