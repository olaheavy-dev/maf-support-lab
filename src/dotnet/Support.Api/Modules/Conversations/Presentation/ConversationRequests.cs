using System.Text.Json;
using System.Text.RegularExpressions;
using Support.Api.Common.Presentation;

namespace Support.Api.Modules.Conversations.Presentation;

/// <summary>
/// Checks request bodies against the contract before anything runs. This is shape validation only;
/// business rules (ownership, idempotency) are the handlers' job.
/// </summary>
internal static partial class ConversationRequests
{
    private const int MaxMessageLength = 4000;

    /// <summary>The create body must be an empty JSON object (or absent): no owner, backend, or identity fields.</summary>
    public static async Task<IResult?> ValidateCreateBodyAsync(HttpRequest request)
    {
        if (request.ContentLength is not > 0 && request.Headers.TransferEncoding.Count == 0)
        {
            return null;
        }

        // invalid_request always lists what is wrong as JSON pointers (contract); "" is the whole body (RFC 6901).
        using var document = await TryParseAsync(request).ConfigureAwait(false);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return ApiResults.InvalidRequest("The request body must be an empty JSON object.", new ProblemField(string.Empty, "must be an empty JSON object"));
        }

        // JsonProperty is a struct: FirstOrDefault() on an empty object is a default value whose Name throws.
        using var properties = document.RootElement.EnumerateObject();
        return properties.MoveNext() && properties.Current.Name is var name
            ? ApiResults.InvalidRequest("The request body must be an empty JSON object.", new ProblemField("/" + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal), "is not allowed: the body must be an empty JSON object"))
            : null;
    }

    public static async Task<(TurnRequest? Request, IResult? Problem)> ReadTurnAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        TurnRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<TurnRequest>(request.Body, ContractJson.Options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException error)
        {
            // Unknown fields (for example customer_id), missing fields, and malformed JSON all land here.
            return (null, Invalid(error.Path is { Length: > 1 } path ? "/" + path.TrimStart('$', '.') : string.Empty, "The body does not match the turn request contract."));
        }

        if (body is null)
        {
            return (null, Invalid(string.Empty, "A request body is required."));
        }

        // [JsonRequired] only demands that the property is present; an explicit null still deserializes.
        if (body.ClientTurnId is null || !ClientTurnIdPattern().IsMatch(body.ClientTurnId))
        {
            return (null, Invalid("/client_turn_id", "must be a UUID"));
        }

        if (body.Message is null || body.Message.Length > MaxMessageLength || string.IsNullOrWhiteSpace(body.Message))
        {
            return (null, Invalid("/message", $"must contain a non-space character and at most {MaxMessageLength} characters"));
        }

        return (body, null);
    }

    /// <summary>Validates an approval decision body: operation_id, decision (approve or reject), optional reason. Nothing else.</summary>
    public static async Task<(ApprovalDecisionRequest? Request, IResult? Problem)> ReadDecisionAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ApprovalDecisionRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<ApprovalDecisionRequest>(request.Body, ContractJson.Options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException error)
        {
            return (null, Invalid(error.Path is { Length: > 1 } path ? "/" + path.TrimStart('$', '.') : string.Empty, "The body does not match the approval decision contract."));
        }

        if (body is null)
        {
            return (null, Invalid(string.Empty, "A request body is required."));
        }

        if (body.OperationId is null || !OperationIdPattern().IsMatch(body.OperationId))
        {
            return (null, Invalid("/operation_id", "must look like op_…"));
        }

        if (body.Decision is not ("approve" or "reject"))
        {
            return (null, Invalid("/decision", "must be approve or reject"));
        }

        if (body.Reason is { } reason && (reason.Length is < 1 or > 500))
        {
            return (null, Invalid("/reason", "must be 1 to 500 characters"));
        }

        return (body, null);
    }

    private static IResult Invalid(string pointer, string message) =>
        ApiResults.InvalidRequest("The request body does not match the contract.", new ProblemField(pointer, message));

    private static async Task<JsonDocument?> TryParseAsync(HttpRequest request)
    {
        try
        {
            return await JsonDocument.ParseAsync(request.Body).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("^op_[A-Za-z0-9]{8,64}$")]
    private static partial Regex OperationIdPattern();

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex ClientTurnIdPattern();
}
