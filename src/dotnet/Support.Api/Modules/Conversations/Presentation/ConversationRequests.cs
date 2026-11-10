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

        using var document = await TryParseAsync(request).ConfigureAwait(false);
        return document is null || document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Any()
            ? ApiResults.InvalidRequest("The request body must be an empty JSON object.")
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

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex ClientTurnIdPattern();
}
