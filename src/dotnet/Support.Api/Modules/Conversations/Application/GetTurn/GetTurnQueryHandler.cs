using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Domain;
using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Identity.Application;

namespace Support.Api.Modules.Conversations.Application.GetTurn;

internal sealed class GetTurnQueryHandler(IConversationStore store, IPrincipalSource principals)
    : IQueryHandler<GetTurnQuery, Turn>
{
    public Task<Result<Turn>> Handle(GetTurnQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var turn = store.FindOwned(query.ConversationId, principals.Current)?.FindTurn(query.TurnId);
        return Task.FromResult(turn is null ? Result.Failure<Turn>(ConversationErrors.NotFound) : Result.Success(turn));
    }
}
