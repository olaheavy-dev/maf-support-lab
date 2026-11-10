using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Domain;
using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Identity.Application;

namespace Support.Api.Modules.Conversations.Application.CreateConversation;

internal sealed class CreateConversationCommandHandler(IConversationStore store, IPrincipalSource principals, TimeProvider clock)
    : ICommandHandler<CreateConversationCommand, Conversation>
{
    public Task<Result<Conversation>> Handle(CreateConversationCommand command, CancellationToken cancellationToken)
    {
        var conversation = Conversation.Start(principals.Current, clock.GetUtcNow());
        store.Add(conversation);
        return Task.FromResult(Result.Success(conversation));
    }
}
