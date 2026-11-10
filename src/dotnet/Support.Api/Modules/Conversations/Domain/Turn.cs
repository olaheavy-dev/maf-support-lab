using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Conversations.Domain;

/// <summary>
/// A finished turn. It completed, paused awaiting the customer's approval of one operation (L10), failed, or was
/// cancelled on request (L12). A turn that resumes after a decision has no client_turn_id and names the turn it resumed
/// from. A turn that failed or was cancelled before routing has no route.
/// </summary>
public sealed record Turn(
    string ConversationId,
    string TurnId,
    string? ClientTurnId,
    TurnRoute? Route,
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

    /// <summary>Why the turn failed (only when <see cref="Outcome"/> is Failed).</summary>
    public TurnError? Error { get; init; }
}

/// <summary>A failed turn's reason (contract turn_error): a code and safe, user-presentable text.</summary>
public sealed record TurnError(string Code, string Detail, int? RetryAfterSeconds = null);

public enum TurnOutcome
{
    Completed,
    AwaitingApproval,
    Failed,
    Cancelled,
}
