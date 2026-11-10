using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Presentation;
using Support.Api.Modules.Conversations.Application.CreateConversation;
using Support.Api.Modules.Conversations.Application.GetConversation;
using Support.Api.Modules.Conversations.Application.GetTurn;
using Support.Api.Modules.Conversations.Application.SubmitTurn;
using Support.Api.Modules.Conversations.Domain;

namespace Support.Api.Modules.Conversations.Presentation;

/// <summary>
/// Version-1 conversation and turn routes (contracts/README.md). Each endpoint only translates:
/// HTTP in, a command or query to its handler, the result back out as contract JSON. Streaming arrives in L12.
/// </summary>
internal static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var conversations = app.MapGroup("/api/v1/conversations");
        conversations.MapPost("/", CreateConversation);
        conversations.MapGet("/{conversationId}", GetConversation);
        conversations.MapPost("/{conversationId}/turns", SubmitTurn);
        conversations.MapGet("/{conversationId}/turns/{turnId}", GetTurn);
        return app;
    }

    private static async Task<IResult> CreateConversation(
        HttpRequest request,
        ICommandHandler<CreateConversationCommand, Conversation> handler,
        CancellationToken cancellationToken)
    {
        if (await ConversationRequests.ValidateCreateBodyAsync(request).ConfigureAwait(false) is { } problem)
        {
            return problem;
        }

        var result = await handler.Handle(new CreateConversationCommand(), cancellationToken).ConfigureAwait(false);
        return result.Match(
            conversation => ApiResults.Ok(ConversationResponse.From(conversation), StatusCodes.Status201Created),
            ApiResults.Problem);
    }

    private static async Task<IResult> GetConversation(
        string conversationId,
        IQueryHandler<GetConversationQuery, Conversation> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetConversationQuery(conversationId), cancellationToken).ConfigureAwait(false);
        return result.Match(conversation => ApiResults.Ok(ConversationResponse.From(conversation)), ApiResults.Problem);
    }

    private static async Task<IResult> SubmitTurn(
        string conversationId,
        HttpRequest request,
        IQueryHandler<GetConversationQuery, Conversation> getConversation,
        ICommandHandler<SubmitTurnCommand, Turn> submitTurn,
        CancellationToken cancellationToken)
    {
        // A conversation you can't see is 404 before anything else, so the body's validity reveals nothing about it.
        var visible = await getConversation.Handle(new GetConversationQuery(conversationId), cancellationToken).ConfigureAwait(false);
        if (visible.IsFailure)
        {
            return ApiResults.Problem(visible.Error);
        }

        var (turnRequest, problem) = await ConversationRequests.ReadTurnAsync(request, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        var result = await submitTurn.Handle(new SubmitTurnCommand(conversationId, turnRequest!.ClientTurnId, turnRequest.Message), cancellationToken).ConfigureAwait(false);
        return result.Match(turn => ApiResults.Ok(TurnResult.From(turn)), ApiResults.Problem);
    }

    private static async Task<IResult> GetTurn(
        string conversationId,
        string turnId,
        IQueryHandler<GetTurnQuery, Turn> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetTurnQuery(conversationId, turnId), cancellationToken).ConfigureAwait(false);
        return result.Match(turn => ApiResults.Ok(TurnResult.From(turn)), ApiResults.Problem);
    }
}
