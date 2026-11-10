using Support.Api.Common.Application;
using Support.Api.Common.Domain;

namespace Support.Api.Common.Presentation;

/// <summary>Builds contract HTTP responses. Every error response in the API goes through here.</summary>
internal static class ApiResults
{
    public static IResult Ok<T>(T body, int statusCode = StatusCodes.Status200OK) =>
        Results.Json(body, ContractJson.Options, statusCode: statusCode);

    /// <summary>Maps an application <see cref="Error"/> to the contract problem with the matching status.</summary>
    public static IResult Problem(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Gone => StatusCodes.Status410Gone,
            _ => StatusCodes.Status500InternalServerError,
        };
        return Problem(status, error.Code, Title(error.Code), error.Description);
    }

    /// <summary>400 invalid_request with the offending field, for bodies that don't match the contract.</summary>
    public static IResult InvalidRequest(string detail, ProblemField? field = null) =>
        Problem(StatusCodes.Status400BadRequest, "invalid_request", Title("invalid_request"), detail, field is null ? null : [field]);

    private static IResult Problem(int status, string code, string title, string? detail, IReadOnlyList<ProblemField>? errors = null) =>
        Results.Json(
            new ContractProblem(ContractVersion.Current, $"https://contracts.maf-support-lab.example/v1/errors/{code}", title, status, code, detail, TraceIds.Current(), errors),
            ContractJson.Options,
            contentType: "application/problem+json",
            statusCode: status);

    private static string Title(string code) => code switch
    {
        "invalid_request" => "Invalid request",
        "not_found" => "Not found",
        "client_turn_id_reused" => "client_turn_id reused",
        "turn_in_progress" => "Turn in progress",
        "approval_mismatch" => "Approval mismatch",
        "approval_already_decided" => "Approval already decided",
        "approval_expired" => "Approval expired",
        _ => "Request failed",
    };
}
