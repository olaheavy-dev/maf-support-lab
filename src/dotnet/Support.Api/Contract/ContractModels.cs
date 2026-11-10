using System.Text.Json.Serialization;

namespace Support.Api.Contract;

/// <summary>Version-1 response shapes from contracts/schemas. Property names are snake_case on the wire.</summary>
public static class ContractVersion
{
    public const string Current = "1.0";
    public const string Backend = "dotnet";
}

public sealed record ConversationResponse(
    string ContractVersion,
    string ConversationId,
    string Backend,
    DateTimeOffset CreatedAt,
    string? ActiveTurnId,
    IReadOnlyList<object> PendingApprovals);

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
    DateTimeOffset FinishedAt);

public sealed record ProblemField([property: JsonPropertyName("pointer")] string JsonPointer, string Message);

public sealed record ContractProblem(
    string ContractVersion,
    string Type,
    string Title,
    int Status,
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TraceId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ProblemField>? Errors = null);
