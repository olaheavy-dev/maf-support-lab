using System.Diagnostics.CodeAnalysis;

namespace Support.Api.Common.Domain;

/// <summary>
/// Why a use case failed, as data rather than an exception. <see cref="Code"/> is the contract error code
/// (contracts/schemas/problem.schema.json), so Presentation can map it to an HTTP problem without guessing.
/// </summary>
[SuppressMessage("Naming", "CA1716", Justification = "C#-only application assembly; Error is the conventional name in the Result pattern.")]
public sealed record Error(string Code, string Description, ErrorType Type)
{
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.Failure);

    public static Error NotFound(string code, string description) => new(code, description, ErrorType.NotFound);

    public static Error Conflict(string code, string description) => new(code, description, ErrorType.Conflict);

    public static Error Validation(string code, string description) => new(code, description, ErrorType.Validation);
}
