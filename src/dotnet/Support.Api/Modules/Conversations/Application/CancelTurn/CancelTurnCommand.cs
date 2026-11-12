using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Domain;
using Support.Api.Modules.Identity.Application;

namespace Support.Api.Modules.Conversations.Application.CancelTurn;

/// <summary>Asks a running turn to stop (contract: cancellation). Returns the turn id once the request is recorded.</summary>
public sealed record CancelTurnCommand(string ConversationId, string TurnId) : ICommand<string>;

/// <summary>
/// Cooperative cancellation: this only signals the turn. The turn stops at its next boundary and starts nothing
/// new; a write that already committed stays committed, and the turn's final outcome reports what actually happened.
/// Someone else's conversation or an unknown turn is not_found; a finished turn is turn_already_finished.
/// </summary>
internal sealed class CancelTurnCommandHandler(IConversationStore store, IPrincipalSource principals) : ICommandHandler<CancelTurnCommand, string>
{
    public Task<Result<string>> Handle(CancelTurnCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var conversation = store.FindOwned(command.ConversationId, principals.Current);
        if (conversation?.FindRunning(command.TurnId) is { } running)
        {
            running.RequestCancel();
            return Task.FromResult(Result.Success(running.TurnId));
        }

        return Task.FromResult(conversation?.FindTurn(command.TurnId) is not null
            ? Result.Failure<string>(ConversationErrors.TurnAlreadyFinished)
            : Result.Failure<string>(ConversationErrors.NotFound));
    }
}
