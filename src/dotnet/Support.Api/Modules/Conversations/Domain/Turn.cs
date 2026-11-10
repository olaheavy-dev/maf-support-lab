namespace Support.Api.Modules.Conversations.Domain;

/// <summary>A finished turn. Until streaming (L12) and approvals (L10) a stored turn is always finished and completed.</summary>
public sealed record Turn(
    string ConversationId,
    string TurnId,
    string ClientTurnId,
    string RouteTarget,
    ReplyKind ReplyKind,
    string ReplyText,
    string TraceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset FinishedAt);

/// <summary>What kind of reply the customer got. Decided from what the tools did, never from the model's wording.</summary>
public enum ReplyKind
{
    Answer,
    NotFound,
    Clarification,
}
