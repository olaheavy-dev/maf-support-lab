using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Domain;
using Support.Api.Modules.Identity.Application;

namespace Support.Api.Modules.Conversations.Application.GetTurn;

/// <summary>
/// The authoritative answer after a dropped stream (contract): a finished turn, or one still running. Only for the
/// conversation's owner; anyone else, and an unknown turn, get not_found.
/// </summary>
internal sealed class GetTurnQueryHandler(IConversationStore store, IPrincipalSource principals)
    : IQueryHandler<GetTurnQuery, TurnStatus>
{
    public Task<Result<TurnStatus>> Handle(GetTurnQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var conversation = store.FindOwned(query.ConversationId, principals.Current);
        var (finished, running) = conversation?.FindForStatus(query.TurnId) ?? (null, null);
        return Task.FromResult(finished is null && running is null
            ? Result.Failure<TurnStatus>(ConversationErrors.NotFound)
            : Result.Success(new TurnStatus(query.ConversationId, finished, running)));
    }
}
