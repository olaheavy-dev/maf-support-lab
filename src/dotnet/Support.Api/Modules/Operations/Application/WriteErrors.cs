using Support.Api.Common.Domain;

namespace Support.Api.Modules.Operations.Application;

/// <summary>Why a write cannot happen. Codes match the contract's operation failure codes where they overlap.</summary>
public static class WriteErrors
{
    public static Error NotEligible(string detail) => Error.Conflict("not_eligible", detail);

    public static readonly Error NotFound = Error.NotFound("not_found", "The order does not exist or is not available to you.");

    public static Error InvalidArguments(string detail) => Error.Validation("invalid_arguments", detail);
}
