using System.Text.Json.Serialization;

namespace Support.Api.Common.Presentation;

/// <summary>The contract's problem-details body (contracts/schemas/problem.schema.json).</summary>
public sealed record ContractProblem(
    string ContractVersion,
    string Type,
    string Title,
    int Status,
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TraceId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ProblemField>? Errors = null);

public sealed record ProblemField([property: JsonPropertyName("pointer")] string JsonPointer, string Message);
