using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Conversations.Domain;

/// <summary>
/// A finished turn. It either completed, or paused awaiting the customer's approval of one operation (L10). A turn that
/// resumes after a decision has no client_turn_id and names the turn it resumed from.
/// </summary>
public sealed record Turn(
    string ConversationId,
    string TurnId,
    string? ClientTurnId,
    TurnRoute Route,
    ReplyKind ReplyKind,
    string ReplyText,
    string TraceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset FinishedAt)
{
    public TurnOutcome Outcome { get; init; } = TurnOutcome.Completed;

    /// <summary>The operation awaiting approval (only when <see cref="Outcome"/> is AwaitingApproval).</summary>
    public string? PendingOperationId { get; init; }

    public string? ResumedFromTurnId { get; init; }

    /// <summary>Operations this turn created or changed.</summary>
    public IReadOnlyList<string> OperationIds { get; init; } = [];
}

public enum TurnOutcome
{
    Completed,
    AwaitingApproval,
}
