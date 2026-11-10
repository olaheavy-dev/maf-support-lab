using System.Text.Json;
using System.Text.RegularExpressions;
using Support.Api.Agents;
using Support.Api.Identity;

namespace Support.Api.Contract;

/// <summary>Version-1 conversation and turn routes (contracts/README.md). JSON responses; streaming arrives in L12.</summary>
internal static partial class ConversationEndpoints
{
    private const int MaxMessageLength = 4000;

    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var conversations = app.MapGroup("/api/v1/conversations");
        conversations.MapPost("/", CreateConversation);
        conversations.MapGet("/{conversationId}", GetConversation);
        conversations.MapPost("/{conversationId}/turns", SubmitTurn);
        conversations.MapGet("/{conversationId}/turns/{turnId}", GetTurn);
        return app;
    }

    private static async Task<IResult> CreateConversation(HttpRequest request, ConversationStore store, IPrincipalSource principals)
    {
        // The create body must be an empty JSON object (or absent): no owner, backend, or identity fields.
        if (request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0)
        {
            using var document = await TryParseAsync(request).ConfigureAwait(false);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Any())
            {
                return ContractJson.Problem(StatusCodes.Status400BadRequest, "invalid_request", "Invalid request", "The request body must be an empty JSON object.");
            }
        }

        var conversation = store.Create(principals.Current);
        return Results.Json(ToResponse(conversation), ContractJson.Options, statusCode: StatusCodes.Status201Created);
    }

    private static IResult GetConversation(string conversationId, ConversationStore store, IPrincipalSource principals)
    {
        var conversation = store.FindOwned(conversationId, principals.Current);
        return conversation is null ? ContractJson.NotFound() : Results.Json(ToResponse(conversation), ContractJson.Options);
    }

    private static async Task<IResult> SubmitTurn(
        string conversationId,
        HttpRequest request,
        ConversationStore store,
        IPrincipalSource principals,
        SupportAgents agents,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var conversation = store.FindOwned(conversationId, principals.Current);
        if (conversation is null)
        {
            return ContractJson.NotFound();
        }

        var (turnRequest, problem) = await ReadTurnRequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        // Idempotent submission: claim the client_turn_id before running, so a duplicate (even one arriving
        // while the first is still running) reuses that single run instead of calling the model again.
        var (reservation, isOwner) = conversation.Reserve(turnRequest!.ClientTurnId, turnRequest.Message);
        if (!string.Equals(reservation.Message, turnRequest.Message, StringComparison.Ordinal))
        {
            return ContractJson.Problem(StatusCodes.Status409Conflict, "client_turn_id_reused", "client_turn_id reused", "This client_turn_id was already used with a different message.");
        }

        if (!isOwner)
        {
            var existing = await reservation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(existing, ContractJson.Options);
        }

        try
        {
            var result = await RunTurnAsync(conversation, turnRequest, agents, clock, cancellationToken).ConfigureAwait(false);
            conversation.Complete(reservation, result);
            return Results.Json(result, ContractJson.Options);
        }
        catch (Exception error)
        {
            conversation.Abandon(turnRequest.ClientTurnId, reservation, error);
            throw;
        }
    }

    private static async Task<TurnResult> RunTurnAsync(
        StoredConversation conversation,
        TurnRequest turnRequest,
        SupportAgents agents,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var createdAt = clock.GetUtcNow();
        using var turn = TurnActivity.Begin();
        var response = await agents.Orders.RunAsync(turnRequest.Message, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new TurnResult(
            ContractVersion.Current,
            conversation.ConversationId,
            ConversationStore.NewId("turn_"),
            turnRequest.ClientTurnId,
            State: "finished",
            Outcome: "completed",
            // Single orders agent until triage arrives in L07.
            new RouteResult("routed", OrdersAgentFactory.AgentName),
            new ReplyResult(ReplyKind(turn.Activity), ReplyText(response.Text)),
            Operations: [],
            ContractJson.TraceId(),
            createdAt,
            clock.GetUtcNow());
    }

    private static IResult GetTurn(string conversationId, string turnId, ConversationStore store, IPrincipalSource principals)
    {
        var turn = store.FindOwned(conversationId, principals.Current)?.FindByTurnId(turnId);
        return turn is null ? ContractJson.NotFound() : Results.Json(turn, ContractJson.Options);
    }

    /// <summary>The reply kind comes from what the tools actually did, not from the model's wording.</summary>
    private static string ReplyKind(TurnActivity activity)
    {
        var lookups = activity.OrderLookups;
        if (lookups.Contains(OrderLookupOutcome.Found))
        {
            return "answer";
        }

        return lookups.Contains(OrderLookupOutcome.NotFound) ? "not_found" : "clarification";
    }

    private static string ReplyText(string text) =>
        string.IsNullOrWhiteSpace(text) ? "Sorry, I couldn't produce an answer. Please try again." : text.Trim();

    private static async Task<(TurnRequest? Request, IResult? Problem)> ReadTurnRequestAsync(HttpRequest request, CancellationToken cancellationToken)
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
        ContractJson.Problem(StatusCodes.Status400BadRequest, "invalid_request", "Invalid request", "The request body does not match the contract.", [new ProblemField(pointer, message)]);

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

    private static ConversationResponse ToResponse(StoredConversation conversation) =>
        new(ContractVersion.Current, conversation.ConversationId, ContractVersion.Backend, conversation.CreatedAt, ActiveTurnId: null, PendingApprovals: []);

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex ClientTurnIdPattern();
}
