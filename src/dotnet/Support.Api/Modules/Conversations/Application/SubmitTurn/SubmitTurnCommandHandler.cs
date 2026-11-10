using System.Diagnostics;
using Support.Api.Common.Application;
using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Application.Observability;
using Support.Api.Common.Domain;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Identity.Application;

namespace Support.Api.Modules.Conversations.Application.SubmitTurn;

/// <summary>
/// Runs a turn exactly once per client_turn_id. Idempotency lives here, in application code, not in the endpoint
/// or the model: a duplicate request (even one arriving while the first still runs) reuses that single run.
/// Each turn is one "support.turn" span (ids as tags), one metric observation (bounded labels), and one log line.
/// No message text or customer id is written to telemetry.
/// </summary>
internal sealed partial class SubmitTurnCommandHandler(
    IConversationStore store,
    IPrincipalSource principals,
    SupportAgents agents,
    TimeProvider clock,
    TurnMetrics metrics,
    ILogger<SubmitTurnCommandHandler> logger) : ICommandHandler<SubmitTurnCommand, Turn>
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

        using var span = SupportTelemetry.ActivitySource.StartActivity("support.turn");
        span?.SetTag("support.conversation.id", command.ConversationId);
        span?.SetTag("support.client_turn.id", command.ClientTurnId);
        var started = clock.GetTimestamp();

        if (!isOwner)
        {
            var existing = await reservation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            span?.SetTag("support.turn.id", existing.TurnId);
            span?.SetTag("support.turn.outcome", "replayed");
            metrics.Record(existing.RouteTarget, "replayed", ReplyKindName(existing.ReplyKind), clock.GetElapsedTime(started));
            return Result.Success(existing);
        }

        try
        {
            var turn = await RunAsync(conversation, command, cancellationToken).ConfigureAwait(false);
            conversation.Complete(reservation, turn);

            var replyKind = ReplyKindName(turn.ReplyKind);
            span?.SetTag("support.turn.id", turn.TurnId);
            span?.SetTag("support.agent", turn.RouteTarget);
            span?.SetTag("support.turn.outcome", "completed");
            span?.SetTag("support.reply_kind", replyKind);
            metrics.Record(turn.RouteTarget, "completed", replyKind, clock.GetElapsedTime(started));
            Log.TurnCompleted(logger, turn.ConversationId, turn.TurnId, turn.RouteTarget, replyKind);
            return Result.Success(turn);
        }
        catch (Exception error)
        {
            conversation.Abandon(command.ClientTurnId, reservation, error);

            span?.SetTag("support.agent", SupportAgents.OrdersAgentName);
            span?.SetTag("support.turn.outcome", "failed");
            span?.SetTag("error.type", error.GetType().FullName);
            span?.SetStatus(ActivityStatusCode.Error, "The turn failed while running the agent.");
            span?.AddException(error);
            metrics.Record(SupportAgents.OrdersAgentName, "failed", replyKind: null, clock.GetElapsedTime(started));
            Log.TurnFailed(logger, error, command.ConversationId, command.ClientTurnId, SupportAgents.OrdersAgentName);
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

    private static string ReplyKindName(ReplyKind kind) => kind switch
    {
        ReplyKind.Answer => "answer",
        ReplyKind.NotFound => "not_found",
        ReplyKind.Clarification => "clarification",
        _ => "unknown",
    };

    private static string ReplyText(string text) =>
        string.IsNullOrWhiteSpace(text) ? "Sorry, I couldn't produce an answer. Please try again." : text.Trim();

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Turn {TurnId} completed in conversation {ConversationId} by agent {Agent} with reply kind {ReplyKind}")]
        public static partial void TurnCompleted(ILogger logger, string conversationId, string turnId, string agent, string replyKind);

        [LoggerMessage(Level = LogLevel.Error, Message = "Turn failed in conversation {ConversationId} (client turn {ClientTurnId}) while running agent {Agent}")]
        public static partial void TurnFailed(ILogger logger, Exception error, string conversationId, string clientTurnId, string agent);
    }
}
