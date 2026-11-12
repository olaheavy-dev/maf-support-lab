using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Presentation;
using Support.Api.Modules.Conversations.Application.CancelTurn;
using Support.Api.Modules.Conversations.Application.CreateConversation;
using Support.Api.Modules.Conversations.Application.DecideApproval;
using Support.Api.Modules.Conversations.Application.GetConversation;
using Support.Api.Modules.Conversations.Application.GetTurn;
using Support.Api.Modules.Conversations.Application.SubmitTurn;
using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;

namespace Support.Api.Modules.Conversations.Presentation;

/// <summary>
/// Version-1 conversation and turn routes (contracts/README.md). Each endpoint only translates:
/// HTTP in, a command or query to its handler, the result back out as contract JSON, or as Server-Sent Events when
/// the client asks for <c>text/event-stream</c>.
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
        conversations.MapPost("/{conversationId}/turns/{turnId}/cancel", CancelTurn);
        conversations.MapPost("/{conversationId}/approvals/{approvalId}", DecideApproval);
        return app;
    }

    private static async Task<IResult> CreateConversation(
        HttpRequest request,
        ICommandHandler<CreateConversationCommand, Conversation> handler,
        IOperationStore operations,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (await ConversationRequests.ValidateCreateBodyAsync(request).ConfigureAwait(false) is { } problem)
        {
            return problem;
        }

        var result = await handler.Handle(new CreateConversationCommand(), cancellationToken).ConfigureAwait(false);
        return result.Match(
            conversation => ApiResults.Ok(ConversationResponse.From(conversation, operations, clock.GetUtcNow()), StatusCodes.Status201Created),
            ApiResults.Problem);
    }

    private static async Task<IResult> GetConversation(
        string conversationId,
        IQueryHandler<GetConversationQuery, Conversation> handler,
        IOperationStore operations,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetConversationQuery(conversationId), cancellationToken).ConfigureAwait(false);
        return result.Match(conversation => ApiResults.Ok(ConversationResponse.From(conversation, operations, clock.GetUtcNow())), ApiResults.Problem);
    }

    private static async Task<IResult> SubmitTurn(
        string conversationId,
        HttpRequest request,
        IQueryHandler<GetConversationQuery, Conversation> getConversation,
        ICommandHandler<SubmitTurnCommand, Turn> submitTurn,
        IOperationStore operations,
        TimeProvider clock,
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

        if (TurnStream.IsRequested(request))
        {
            return await TurnStream.RunAsync(
                observer => submitTurn.Handle(new SubmitTurnCommand(conversationId, turnRequest!.ClientTurnId, turnRequest.Message, observer), cancellationToken),
                operations,
                clock,
                cancellationToken).ConfigureAwait(false);
        }

        var result = await submitTurn.Handle(new SubmitTurnCommand(conversationId, turnRequest!.ClientTurnId, turnRequest.Message), cancellationToken).ConfigureAwait(false);
        return result.Match(turn => ApiResults.Ok(TurnResult.From(turn, operations)), ApiResults.Problem);
    }

    private static async Task<IResult> GetTurn(
        string conversationId,
        string turnId,
        IQueryHandler<GetTurnQuery, TurnStatus> handler,
        IOperationStore operations,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetTurnQuery(conversationId, turnId), cancellationToken).ConfigureAwait(false);
        return result.Match(
            status => ApiResults.Ok(status.Finished is { } finished ? TurnResult.From(finished, operations) : TurnResult.Running(status.ConversationId, status.Running!)),
            ApiResults.Problem);
    }

    private static async Task<IResult> CancelTurn(
        string conversationId,
        string turnId,
        ICommandHandler<CancelTurnCommand, string> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new CancelTurnCommand(conversationId, turnId), cancellationToken).ConfigureAwait(false);
        return result.Match(
            cancelled => ApiResults.Ok(new CancelTurnResponse(ContractVersion.Current, cancelled, CancelRequested: true), StatusCodes.Status202Accepted),
            ApiResults.Problem);
    }

    private static async Task<IResult> DecideApproval(
        string conversationId,
        string approvalId,
        HttpRequest request,
        IQueryHandler<GetConversationQuery, Conversation> getConversation,
        ICommandHandler<DecideApprovalCommand, Turn> decide,
        IOperationStore operations,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // A conversation you can't see is 404 before anything else, as for turns.
        var visible = await getConversation.Handle(new GetConversationQuery(conversationId), cancellationToken).ConfigureAwait(false);
        if (visible.IsFailure)
        {
            return ApiResults.Problem(visible.Error);
        }

        var (body, problem) = await ConversationRequests.ReadDecisionAsync(request, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        var decision = body!.Decision == "approve" ? ApprovalDecision.Approve : ApprovalDecision.Reject;
        if (TurnStream.IsRequested(request))
        {
            return await TurnStream.RunAsync(
                observer => decide.Handle(new DecideApprovalCommand(conversationId, approvalId, body.OperationId, decision, body.Reason, observer), cancellationToken),
                operations,
                clock,
                cancellationToken).ConfigureAwait(false);
        }

        var result = await decide.Handle(new DecideApprovalCommand(conversationId, approvalId, body.OperationId, decision, body.Reason), cancellationToken).ConfigureAwait(false);
        return result.Match(turn => ApiResults.Ok(TurnResult.From(turn, operations)), ApiResults.Problem);
    }
}

/// <summary>202 body for a cancel request: recorded, not yet necessarily observed by the turn.</summary>
public sealed record CancelTurnResponse(string ContractVersion, string TurnId, bool CancelRequested);
