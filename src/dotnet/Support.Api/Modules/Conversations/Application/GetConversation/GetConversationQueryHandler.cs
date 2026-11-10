using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Domain;
using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Identity.Application;

namespace Support.Api.Modules.Conversations.Application.GetConversation;

internal sealed class GetConversationQueryHandler(IConversationStore store, IPrincipalSource principals)
    : IQueryHandler<GetConversationQuery, Conversation>
{
    public Task<Result<Conversation>> Handle(GetConversationQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var conversation = store.FindOwned(query.ConversationId, principals.Current);
        return Task.FromResult(conversation is null
            ? Result.Failure<Conversation>(ConversationErrors.NotFound)
            : Result.Success(conversation));
    }
}
