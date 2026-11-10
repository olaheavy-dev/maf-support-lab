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
internal sealed class DecideApprovalCommandHandler(
    IConversationStore conversations,
    IOperationStore operations,
    IPrincipalSource principals,
    ISupportRouter router,
    TimeProvider clock) : ICommandHandler<DecideApprovalCommand, Turn>
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

        if (operation.Decision is null && operation.ExpireIfDue(clock.GetUtcNow()))
        {
            return Result.Failure<Turn>(ConversationErrors.ApprovalExpired);
        }

        // One decision per approval: its key makes a repeated decision a duplicate (replay), never a second run.
        var (claimed, outcome) = conversation.Reserve($"approval:{operation.ApprovalId}", OperationNames.Of(command.Decision));
        if (outcome == ReservationOutcome.Busy)
        {
            return Result.Failure<Turn>(ConversationErrors.TurnInProgress);
        }

        var reservation = claimed!;
        if (outcome == ReservationOutcome.Duplicate)
        {
            return reservation.Message == OperationNames.Of(command.Decision)
                ? Result.Success(await reservation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false))
                : Result.Failure<Turn>(ConversationErrors.ApprovalAlreadyDecided);
        }

        var createdAt = clock.GetUtcNow();
        if (!operation.TryDecide(command.Decision, createdAt))
        {
            conversation.Abandon($"approval:{operation.ApprovalId}", reservation, new InvalidOperationException("Already decided."));
            return Result.Failure<Turn>(operation.Status == OperationStatus.Expired ? ConversationErrors.ApprovalExpired : ConversationErrors.ApprovalAlreadyDecided);
        }

        try
        {
            var reply = await router.ResumeAsync(new ApprovalResume(operation.OperationId, command.Reason), cancellationToken).ConfigureAwait(false);
            var turn = new Turn(
                conversation.ConversationId, reservation.TurnId, ClientTurnId: null, reply.Route, reply.Kind, reply.Text,
                TraceIds.Current(), createdAt, clock.GetUtcNow())
            {
                ResumedFromTurnId = operation.SourceTurnId,
                OperationIds = [operation.OperationId],
            };
            conversation.Complete(reservation, turn, reply);
            operation.RecordDecisionTurn(turn.TurnId);
            return Result.Success(turn);
        }
        catch (Exception error)
        {
            if (operation.Status == OperationStatus.Running)
            {
                operation.Fail("internal_error", "The approved action could not be completed.", clock.GetUtcNow());
            }

            conversation.Abandon($"approval:{operation.ApprovalId}", reservation, error);
            throw;
        }
    }
}
