using Support.Api.Common.Application;
using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Domain;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Identity.Application;

namespace Support.Api.Modules.Conversations.Application.SubmitTurn;

/// <summary>
/// Runs a turn exactly once per client_turn_id. Idempotency lives here, in application code, not in the endpoint
/// or the model: a duplicate request (even one arriving while the first still runs) reuses that single run.
/// </summary>
internal sealed class SubmitTurnCommandHandler(
    IConversationStore store,
    IPrincipalSource principals,
    SupportAgents agents,
    TimeProvider clock) : ICommandHandler<SubmitTurnCommand, Turn>
{
    public async Task<Result<Turn>> Handle(SubmitTurnCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var conversation = store.FindOwned(command.ConversationId, principals.Current);
        if (conversation is null)
        {
            return Result.Failure<Turn>(ConversationErrors.NotFound);
        }

        // Claim the client_turn_id before running, so a duplicate never calls the model again.
        var (reservation, isOwner) = conversation.Reserve(command.ClientTurnId, command.Message);
        if (!string.Equals(reservation.Message, command.Message, StringComparison.Ordinal))
        {
            return Result.Failure<Turn>(ConversationErrors.ClientTurnIdReused);
        }

        if (!isOwner)
        {
            return Result.Success(await reservation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false));
        }

        try
        {
            var turn = await RunAsync(conversation, command, cancellationToken).ConfigureAwait(false);
            conversation.Complete(reservation, turn);
            return Result.Success(turn);
        }
        catch (Exception error)
        {
            conversation.Abandon(command.ClientTurnId, reservation, error);
            throw;
        }
    }

    private async Task<Turn> RunAsync(Conversation conversation, SubmitTurnCommand command, CancellationToken cancellationToken)
    {
        var createdAt = clock.GetUtcNow();
        using var activity = TurnActivity.Begin();
        var response = await agents.Orders.RunAsync(command.Message, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new Turn(
            conversation.ConversationId,
            Ids.New("turn_"),
            command.ClientTurnId,
            // Single orders agent until triage arrives in L07.
            SupportAgents.OrdersAgentName,
            ReplyKindFrom(activity.Activity),
            ReplyText(response.Text),
            TraceIds.Current(),
            createdAt,
            clock.GetUtcNow());
    }

    /// <summary>The reply kind comes from what the tools actually did, not from the model's wording.</summary>
    private static ReplyKind ReplyKindFrom(TurnActivity activity)
    {
        var lookups = activity.OrderLookups;
        if (lookups.Contains(OrderLookupOutcome.Found))
        {
            return ReplyKind.Answer;
        }

        return lookups.Contains(OrderLookupOutcome.NotFound) ? ReplyKind.NotFound : ReplyKind.Clarification;
    }

    private static string ReplyText(string text) =>
        string.IsNullOrWhiteSpace(text) ? "Sorry, I couldn't produce an answer. Please try again." : text.Trim();
}
