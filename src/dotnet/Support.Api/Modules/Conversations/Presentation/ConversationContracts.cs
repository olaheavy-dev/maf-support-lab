using System.Text.Json.Serialization;
using Support.Api.Common.Presentation;
using Support.Api.Modules.Conversations.Domain;

namespace Support.Api.Modules.Conversations.Presentation;

/// <summary>Version-1 request and response shapes from contracts/schemas. Property names are snake_case on the wire.</summary>
public sealed record ConversationResponse(
    string ContractVersion,
    string ConversationId,
    string Backend,
    DateTimeOffset CreatedAt,
    string? ActiveTurnId,
    IReadOnlyList<object> PendingApprovals)
{
    public static ConversationResponse From(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return new(Common.Presentation.ContractVersion.Current, conversation.ConversationId, Common.Presentation.ContractVersion.Backend, conversation.CreatedAt, ActiveTurnId: null, PendingApprovals: []);
    }
}

public sealed record TurnRequest(
    [property: JsonRequired] string ClientTurnId,
    [property: JsonRequired] string Message);

public sealed record RouteResult(
    string Status,
    string? Target,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UnroutedReason = null);

public sealed record ReplyResult(string Kind, string Text);

public sealed record TurnResult(
    string ContractVersion,
    string ConversationId,
    string TurnId,
    string? ClientTurnId,
    string State,
    string Outcome,
    RouteResult Route,
    ReplyResult Reply,
    IReadOnlyList<object> Operations,
    string TraceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset FinishedAt)
{
    public static TurnResult From(Turn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return new(
            Common.Presentation.ContractVersion.Current,
            turn.ConversationId,
            turn.TurnId,
            turn.ClientTurnId,
            State: "finished",
            Outcome: "completed",
            new RouteResult("routed", turn.RouteTarget),
            new ReplyResult(ReplyKindName(turn.ReplyKind), turn.ReplyText),
            Operations: [],
            turn.TraceId,
            turn.CreatedAt,
            turn.FinishedAt);
    }

    private static string ReplyKindName(ReplyKind kind) => kind switch
    {
        ReplyKind.Answer => "answer",
        ReplyKind.NotFound => "not_found",
        ReplyKind.Clarification => "clarification",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown reply kind."),
    };
}
