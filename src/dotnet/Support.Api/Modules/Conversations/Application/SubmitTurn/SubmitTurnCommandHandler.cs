using System.Diagnostics;
using Support.Api.Common.Application;
using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Application.Observability;
using Support.Api.Common.Application.Safety;
using Support.Api.Common.Application.Streaming;
using Support.Api.Common.Domain;
using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Identity.Application;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Triage.Application;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Conversations.Application.SubmitTurn;

/// <summary>
/// Runs a turn exactly once per client_turn_id. Idempotency lives here, in application code, not in the endpoint
/// or the model: a duplicate request (even one arriving while the first still runs) reuses that single run.
/// Since L12 the run is detached from the HTTP request: it has its own cancellation, which only a cancel request
/// triggers, so a client that disconnects neither stops nor undoes it, and the finished turn (completed, awaiting
/// approval, failed, or cancelled) is stored for a status lookup. The request only waits for it.
/// Each turn is one "support.turn" span (ids as tags), one metric observation (bounded labels), and one log line.
/// No message text or customer id is written to telemetry.
/// </summary>
internal sealed partial class SubmitTurnCommandHandler(
    IConversationStore store,
    IOperationStore operations,
    IPrincipalSource principals,
    ISupportRouter router,
    TimeProvider clock,
    TurnMetrics metrics,
    InjectionSignals injection,
    ILogger<SubmitTurnCommandHandler> logger) : ICommandHandler<SubmitTurnCommand, Turn>
{
    /// <summary>What a cancelled turn says. Contract example: nothing further was done.</summary>
    public const string CancelledText = "Stopped. Nothing was changed.";

    public async Task<Result<Turn>> Handle(SubmitTurnCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var principal = principals.Current; // read once: the same principal checks ownership and runs the turn
        var conversation = store.FindOwned(command.ConversationId, principal);
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

        if (outcome == ReservationOutcome.Duplicate)
        {
            // The same submission again: report the one run (no replay of its live events) and wait for its result.
            command.Observer.Started(new(command.ConversationId, reservation.TurnId, command.ClientTurnId, null));
            var started = clock.GetTimestamp();
            var existing = await reservation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            metrics.Record(RouteLabel(existing.Route), "replayed", Names.Of(existing.ReplyKind), clock.GetElapsedTime(started));
            return Result.Success(existing);
        }

        reservation.ClientTurnId = command.ClientTurnId;
        reservation.StartedAt = clock.GetUtcNow();
        command.Observer.Started(new(command.ConversationId, reservation.TurnId, command.ClientTurnId, null));
        // The caller's principal is pinned for the whole detached run (L13): identity never changes mid-turn and never
        // depends on a request that has ended.
        var caller = principal;
        _ = Task.Run(
            async () =>
            {
                using (PrincipalScope.Begin(caller))
                {
                    await ExecuteAsync(conversation, reservation, command).ConfigureAwait(false);
                }
            },
            CancellationToken.None);
        return Result.Success(await reservation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The detached run. It always finishes the reservation: with a stored turn, or (if storing fails) by releasing it.</summary>
    private async Task ExecuteAsync(Conversation conversation, TurnReservation reservation, SubmitTurnCommand command)
    {
        using var span = SupportTelemetry.ActivitySource.StartActivity("support.turn");
        span?.SetTag("support.conversation.id", command.ConversationId);
        span?.SetTag("support.client_turn.id", command.ClientTurnId);
        span?.SetTag("support.turn.id", reservation.TurnId);
        reservation.TraceId = TraceIds.Current();
        injection.Inspect("message", command.Message); // diagnostic only: the turn runs exactly the same either way
        var started = clock.GetTimestamp();
        var lastRoute = new RouteCapture(command.Observer);

        Turn turn;
        SupportReply? reply = null;
        try
        {
            // This conversation's turn is ours now: close approvals left by a turn that was never stored (L11).
            OrphanedApprovals.Expire(conversation, operations, clock.GetUtcNow());
            reservation.CancellationToken.ThrowIfCancellationRequested();
            reply = await router.RouteAsync(
                new RoutingRequest(conversation.ConversationId, command.Message, conversation.Context) { TurnId = reservation.TurnId },
                lastRoute,
                reservation.CancellationToken).ConfigureAwait(false);
            turn = Finished(conversation, reservation, command, reply.Route, reply.Kind, reply.Text) with { };
            turn = turn with
            {
                Outcome = reply.PendingOperationId is null ? TurnOutcome.Completed : TurnOutcome.AwaitingApproval,
                PendingOperationId = reply.PendingOperationId,
                OperationIds = reply.OperationIds,
            };
        }
        catch (Exception) when (reservation.CancelRequested)
        {
            // Cancelled on request at a boundary: nothing further was done (a turn without a write changes nothing).
            reply = null;
            turn = Finished(conversation, reservation, command, lastRoute.Route, ReplyKind.Answer, CancelledText) with { Outcome = TurnOutcome.Cancelled };
        }
        catch (Exception error)
        {
            // A turn that ran and failed is a result, not an HTTP error (contract): stored, and replayed on a repeat.
            reply = null;
            span?.SetStatus(ActivityStatusCode.Error, "The turn failed while running the agent.");
            span?.SetTag("error.type", error.GetType().FullName);
            span?.AddException(error);
            Log.TurnFailed(logger, error, command.ConversationId, command.ClientTurnId);
            var failure = TurnErrors.From(error);
            turn = Finished(conversation, reservation, command, lastRoute.Route, ReplyKind.Answer, failure.Detail) with { Outcome = TurnOutcome.Failed, Error = failure };
        }

        try
        {
            if (turn.Outcome is TurnOutcome.Failed or TurnOutcome.Cancelled)
            {
                OrphanedApprovals.ExpireFromTurn(conversation.ConversationId, reservation.TurnId, operations, clock.GetUtcNow());
            }

            turn = store.SaveTurn(conversation.ConversationId, command.ClientTurnId, reservation.Message, turn, reply is null ? conversation.Context : conversation.Context.After(reply));
            conversation.Complete(reservation, turn, reply);
        }
        catch (Exception error)
        {
            // Not stored, so not finished: release the client_turn_id so the client can retry it.
            conversation.Abandon(command.ClientTurnId, reservation, error);
            span?.SetStatus(ActivityStatusCode.Error, "The finished turn could not be stored.");
            span?.AddException(error);
            metrics.Record("unknown", "failed", replyKind: null, clock.GetElapsedTime(started));
            Log.TurnFailed(logger, error, command.ConversationId, command.ClientTurnId);
            return;
        }

        var outcomeName = OutcomeName(turn.Outcome);
        var replyKind = turn.Outcome is TurnOutcome.Failed ? null : Names.Of(turn.ReplyKind);
        var route = RouteLabel(turn.Route);
        span?.SetTag("support.agent", route);
        span?.SetTag("support.turn.outcome", outcomeName);
        if (replyKind is not null)
        {
            span?.SetTag("support.reply_kind", replyKind);
        }

        metrics.Record(route, outcomeName, replyKind, clock.GetElapsedTime(started));
        Log.TurnCompleted(logger, turn.ConversationId, turn.TurnId, route, outcomeName);
    }

    private Turn Finished(Conversation conversation, TurnReservation reservation, SubmitTurnCommand command, TurnRoute? route, ReplyKind kind, string text) =>
        new(conversation.ConversationId, reservation.TurnId, command.ClientTurnId, route, kind, text, reservation.TraceId, reservation.StartedAt, clock.GetUtcNow());

    internal static string OutcomeName(TurnOutcome outcome) => outcome switch
    {
        TurnOutcome.AwaitingApproval => "awaiting_approval",
        TurnOutcome.Failed => "failed",
        TurnOutcome.Cancelled => "cancelled",
        _ => "completed",
    };

    /// <summary>The bounded "agent" label: the routed target, or "unrouted".</summary>
    private static string RouteLabel(TurnRoute? route) => route is null ? "unknown" : route.Target is { } target ? Names.Of(target) : "unrouted";

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Turn {TurnId} finished in conversation {ConversationId}, route {Route}, outcome {Outcome}")]
        public static partial void TurnCompleted(ILogger logger, string conversationId, string turnId, string route, string outcome);

        [LoggerMessage(Level = LogLevel.Error, Message = "Turn failed in conversation {ConversationId} (client turn {ClientTurnId})")]
        public static partial void TurnFailed(ILogger logger, Exception error, string conversationId, string clientTurnId);
    }

    /// <summary>Passes events through and remembers the route triage chose, so a turn that fails later still reports it.</summary>
    private sealed class RouteCapture(ITurnObserver inner) : ITurnObserver
    {
        public TurnRoute? Route { get; private set; }

        public void Started(TurnStarted started) => inner.Started(started);

        public void Progress(TurnProgress progress)
        {
            ArgumentNullException.ThrowIfNull(progress);
            if (progress.Stage == TurnStage.Routed)
            {
                Route = progress.RouteTarget is { } target && Names.TryParseTarget(target, out var routed) ? TurnRoute.Routed(routed)
                    : TurnRoute.Unrouted(progress.UnroutedReason == Names.Of(UnroutedReason.InvalidModelOutput) ? UnroutedReason.InvalidModelOutput : UnroutedReason.AmbiguousRequest);
            }

            inner.Progress(progress);
        }

        public void Delta(string text) => inner.Delta(text);
    }
}
