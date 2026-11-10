using System.Diagnostics;
using Support.Api.Common.Application;
using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Application.Observability;
using Support.Api.Common.Domain;
using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Identity.Application;
using Support.Api.Modules.Triage.Application;
using Support.Api.Modules.Triage.Domain;

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
    ISupportRouter router,
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

        // Claim the client_turn_id before running, so a duplicate never calls the model again, and only one turn
        // runs per conversation at a time (a different turn arriving meanwhile is rejected, not queued).
        var (claimed, outcome) = conversation.Reserve(command.ClientTurnId, command.Message);
        if (outcome == ReservationOutcome.Busy)
        {
            return Result.Failure<Turn>(ConversationErrors.TurnInProgress);
        }

        var reservation = claimed!;
        if (!string.Equals(reservation.Message, command.Message, StringComparison.Ordinal))
        {
            return Result.Failure<Turn>(ConversationErrors.ClientTurnIdReused);
        }

        using var span = SupportTelemetry.ActivitySource.StartActivity("support.turn");
        span?.SetTag("support.conversation.id", command.ConversationId);
        span?.SetTag("support.client_turn.id", command.ClientTurnId);
        var started = clock.GetTimestamp();

        if (outcome == ReservationOutcome.Duplicate)
        {
            var existing = await reservation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            span?.SetTag("support.turn.id", existing.TurnId);
            span?.SetTag("support.turn.outcome", "replayed");
            metrics.Record(RouteLabel(existing.Route), "replayed", Names.Of(existing.ReplyKind), clock.GetElapsedTime(started));
            return Result.Success(existing);
        }

        try
        {
            var (turn, reply) = await RunAsync(conversation, reservation, command, cancellationToken).ConfigureAwait(false);
            conversation.Complete(reservation, turn, reply);

            var replyKind = Names.Of(turn.ReplyKind);
            var route = RouteLabel(turn.Route);
            span?.SetTag("support.turn.id", turn.TurnId);
            span?.SetTag("support.agent", route);
            span?.SetTag("support.turn.outcome", "completed");
            span?.SetTag("support.reply_kind", replyKind);
            metrics.Record(route, "completed", replyKind, clock.GetElapsedTime(started));
            Log.TurnCompleted(logger, turn.ConversationId, turn.TurnId, route, replyKind);
            return Result.Success(turn);
        }
        catch (Exception error)
        {
            conversation.Abandon(command.ClientTurnId, reservation, error);

            span?.SetTag("support.turn.outcome", "failed");
            span?.SetTag("error.type", error.GetType().FullName);
            span?.SetStatus(ActivityStatusCode.Error, "The turn failed while running the agent.");
            span?.AddException(error);
            metrics.Record("unknown", "failed", replyKind: null, clock.GetElapsedTime(started));
            Log.TurnFailed(logger, error, command.ConversationId, command.ClientTurnId);
            throw;
        }
    }

    private async Task<(Turn Turn, SupportReply Reply)> RunAsync(
        Conversation conversation, TurnReservation reservation, SubmitTurnCommand command, CancellationToken cancellationToken)
    {
        var createdAt = clock.GetUtcNow();
        // Triage, then the specialist the routing rules choose (L07), with this conversation's native sessions and
        // the verified facts from its earlier turns (L09).
        var reply = await router.RouteAsync(new RoutingRequest(conversation.ConversationId, command.Message, conversation.Context), cancellationToken)
            .ConfigureAwait(false);

        var turn = new Turn(
            conversation.ConversationId,
            reservation.TurnId,
            command.ClientTurnId,
            reply.Route,
            reply.Kind,
            reply.Text,
            TraceIds.Current(),
            createdAt,
            clock.GetUtcNow());
        return (turn, reply);
    }

    /// <summary>The bounded "agent" label: the routed target, or "unrouted".</summary>
    private static string RouteLabel(TurnRoute route) => route.Target is { } target ? Names.Of(target) : "unrouted";

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Turn {TurnId} completed in conversation {ConversationId}, route {Route}, reply kind {ReplyKind}")]
        public static partial void TurnCompleted(ILogger logger, string conversationId, string turnId, string route, string replyKind);

        [LoggerMessage(Level = LogLevel.Error, Message = "Turn failed in conversation {ConversationId} (client turn {ClientTurnId})")]
        public static partial void TurnFailed(ILogger logger, Exception error, string conversationId, string clientTurnId);
    }
}
