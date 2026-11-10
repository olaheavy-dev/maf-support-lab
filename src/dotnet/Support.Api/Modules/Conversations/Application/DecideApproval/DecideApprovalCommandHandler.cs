using Support.Api.Common.Application;
using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Domain;
using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Identity.Application;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;
using Support.Api.Modules.Triage.Application;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Conversations.Application.DecideApproval;

/// <summary>
/// Applies the customer's decision and resumes the paused work as a new turn (contract: approvals). The approval is
/// bound to this customer and this conversation (anyone else: 404), to one operation (wrong id: 409 approval_mismatch),
/// and to one decision (a different one later: 409 approval_already_decided; too late: 410 approval_expired).
/// Repeating the same decision returns the same turn and never runs the write twice.
/// </summary>
internal sealed partial class DecideApprovalCommandHandler(
    IConversationStore conversations,
    IOperationStore operations,
    IPrincipalSource principals,
    ISupportRouter router,
    TimeProvider clock,
    ILogger<DecideApprovalCommandHandler> logger) : ICommandHandler<DecideApprovalCommand, Turn>
{
    public async Task<Result<Turn>> Handle(DecideApprovalCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var principal = principals.Current;
        var conversation = conversations.FindOwned(command.ConversationId, principal);
        var operation = operations.FindByApproval(command.ApprovalId);
        if (conversation is null || operation is null || operation.ConversationId != conversation.ConversationId || operation.OwnerCustomerId != principal.CustomerId)
        {
            return Result.Failure<Turn>(ConversationErrors.NotFound);
        }

        if (!string.Equals(operation.OperationId, command.OperationId, StringComparison.Ordinal))
        {
            return Result.Failure<Turn>(ConversationErrors.ApprovalMismatch);
        }

        if (operation.Decision is { } recorded && recorded != command.Decision)
        {
            return Result.Failure<Turn>(ConversationErrors.ApprovalAlreadyDecided);
        }

        if (OrphanedApprovals.IsOrphan(conversation, operation))
        {
            // The turn that asked for it was never stored, so the customer could not have been asked: never runnable.
            OrphanedApprovals.Expire(operation, operations, clock.GetUtcNow());
            return Result.Failure<Turn>(ConversationErrors.ApprovalExpired);
        }

        if (operation.Decision is null && operation.ExpireIfDue(clock.GetUtcNow()))
        {
            operations.TryUpdate(operation, OperationStatus.AwaitingApproval);
            return Result.Failure<Turn>(ConversationErrors.ApprovalExpired);
        }

        // One decision per approval: its key makes a repeated decision a duplicate (replay), never a second run.
        var key = $"approval:{operation.ApprovalId}";
        var (claimed, outcome) = conversation.Reserve(key, OperationNames.Of(command.Decision));
        if (outcome == ReservationOutcome.Busy)
        {
            return Result.Failure<Turn>(ConversationErrors.TurnInProgress);
        }

        var reservation = claimed!;
        if (outcome == ReservationOutcome.Duplicate)
        {
            if (reservation.Message != OperationNames.Of(command.Decision))
            {
                return Result.Failure<Turn>(ConversationErrors.ApprovalAlreadyDecided);
            }

            command.Observer.Started(new(conversation.ConversationId, reservation.TurnId, null, operation.SourceTurnId));
            return Result.Success(await reservation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false));
        }

        // From here on the reservation is ours. If anything fails before the turn completes (recording the decision,
        // saving the turn), release it (Codex review on PR #77): otherwise a repeat would wait forever and every other turn
        // would get turn_in_progress. Whatever was stored stays stored, so a retry finishes from the operation record and
        // never runs the write again.
        bool recordedEarlier;
        try
        {
            // A decision already recorded, but whose turn was never stored (the process stopped in between), is finished now
            // from the record (L11): the write is never run a second time.
            recordedEarlier = operation.Decision == command.Decision;
            if (!recordedEarlier && !(operation.TryDecide(command.Decision, clock.GetUtcNow()) && operations.TryUpdate(operation, OperationStatus.AwaitingApproval)))
            {
                conversation.Abandon(key, reservation, new InvalidOperationException("Already decided."));
                return Result.Failure<Turn>(operation.Status == OperationStatus.Expired ? ConversationErrors.ApprovalExpired : ConversationErrors.ApprovalAlreadyDecided);
            }
        }
        catch (Exception error) when (!reservation.Completion.Task.IsCompleted)
        {
            conversation.Abandon(key, reservation, error);
            throw;
        }

        // The decision is recorded. The resumed run is detached from this request (L12): only a cancel request stops it.
        reservation.StartedAt = clock.GetUtcNow();
        reservation.ResumedFromTurnId = operation.SourceTurnId;
        command.Observer.Started(new(conversation.ConversationId, reservation.TurnId, null, operation.SourceTurnId));
        var decided = operation;
        _ = Task.Run(() => ResumeAsync(conversation, key, reservation, decided, recordedEarlier, command), CancellationToken.None);
        return Result.Success(await reservation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The detached resumed run. It always finishes the reservation: with a stored turn, or by releasing it.</summary>
    private async Task ResumeAsync(Conversation conversation, string key, TurnReservation reservation, Operation operation, bool recordedEarlier, DecideApprovalCommand command)
    {
        reservation.TraceId = TraceIds.Current();
        try
        {
            var outcome = TurnOutcome.Completed;
            SupportReply reply;
            try
            {
                // Resume the paused run, unless the decision was recorded earlier and the operation already finished. An
                // approved operation still "running" after a restart changed nothing yet (its write and its commit are one
                // transaction), so resuming it is safe; the guarded commit stops it from ever applying twice.
                reservation.CancellationToken.ThrowIfCancellationRequested();
                reply = recordedEarlier && operation.Status != OperationStatus.Running
                    ? Factual(operation)
                    : await router.ResumeAsync(new ApprovalResume(operation.OperationId, command.Reason), command.Observer, reservation.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // The decision is recorded and the write may already have committed, so this turn must still finish, with
                // the operation's factual state, and stay replayable. Never abandon it: a repeat would then be refused
                // forever, and never run the write again (Codex review on PR #76).
                operation = operations.Find(operation.OperationId) ?? operation;
                if (operation.Status == OperationStatus.Running)
                {
                    // Nothing was applied (the write and its commit are one transaction). A cancel seen before the commit
                    // stops it (L12); anything else is a failure. Either way the operation's record says so.
                    var cancelled = reservation.CancelRequested;
                    if (!cancelled)
                    {
                        Log.ResumeFailed(logger, error, operation.OperationId);
                    }

                    operation.Fail(
                        cancelled ? "cancelled" : "internal_error",
                        cancelled ? "The turn was cancelled before anything was changed." : "The approved action could not be completed.",
                        clock.GetUtcNow());
                    operations.TryUpdate(operation, OperationStatus.Running);
                    outcome = cancelled ? TurnOutcome.Cancelled : TurnOutcome.Completed;
                }
                else if (!reservation.CancelRequested)
                {
                    Log.ResumeFailed(logger, error, operation.OperationId);
                }

                // A cancel that arrives after the commit (or after a rejection) changes nothing: the turn reports the
                // factual, finished state of the operation.
                reply = Factual(operation);
            }

            var finished = new Turn(
                conversation.ConversationId, reservation.TurnId, ClientTurnId: null, reply.Route, reply.Kind, reply.Text,
                reservation.TraceId, reservation.StartedAt, clock.GetUtcNow())
            {
                Outcome = outcome,
                ResumedFromTurnId = operation.SourceTurnId,
                OperationIds = [operation.OperationId],
            };
            var turn = conversations.SaveTurn(conversation.ConversationId, key, reservation.Message, finished, conversation.Context.After(reply));
            conversation.Complete(reservation, turn, reply);
            operation = operations.Find(operation.OperationId) ?? operation;
            operation.RecordDecisionTurn(turn.TurnId);
            operations.TryUpdate(operation, operation.Status);
        }
        catch (Exception error) when (!reservation.Completion.Task.IsCompleted)
        {
            conversation.Abandon(key, reservation, error);
            Log.ResumeFailed(logger, error, operation.OperationId);
        }
    }

    private static SupportReply Factual(Operation operation) =>
        new(TurnRoute.Routed(operation.Specialist), ReplyKind.ApprovalResult, FactualText(operation)) { OperationIds = [operation.OperationId] };

    /// <summary>What the customer is told when the resumed run failed: only what the operation record says happened.</summary>
    private static string FactualText(Operation operation) => operation.Status switch
    {
        OperationStatus.Committed => $"Done. {operation.Summary} (I couldn't write a fuller reply.)",
        OperationStatus.Rejected => $"Okay, I did not go ahead. Nothing has been changed: {operation.Summary}",
        OperationStatus.Failed when operation.Failure?.Code == "cancelled" => "Stopped before anything was changed. Nothing has been changed.",
        OperationStatus.Failed => $"I couldn't complete that: {operation.Failure?.Detail} Nothing has been changed.",
        _ => "That request was not carried out. Nothing has been changed.",
    };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "Resuming approved operation {OperationId} failed; the turn reports the operation's recorded state")]
        public static partial void ResumeFailed(ILogger logger, Exception error, string operationId);
    }
}
