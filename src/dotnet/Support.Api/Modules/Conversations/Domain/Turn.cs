using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Conversations.Domain;

/// <summary>A finished turn. Until streaming (L12) and approvals (L10) a stored turn is always finished and completed.</summary>
public sealed record Turn(
    string ConversationId,
    string TurnId,
    string ClientTurnId,
    TurnRoute Route,
    ReplyKind ReplyKind,
    string ReplyText,
    string TraceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset FinishedAt);
