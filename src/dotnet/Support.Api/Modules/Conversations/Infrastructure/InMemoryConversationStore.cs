using System.Collections.Concurrent;
using Support.Api.Modules.Conversations.Application;
using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Identity.Domain;

namespace Support.Api.Modules.Conversations.Infrastructure;

/// <summary>Process-memory conversations: lost on restart. L11 replaces this class, not its callers.</summary>
internal sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, Conversation> conversations = new(StringComparer.Ordinal);

    public void Add(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        conversations[conversation.ConversationId] = conversation;
    }

    public Conversation? FindOwned(string conversationId, SupportPrincipal principal) =>
        conversations.TryGetValue(conversationId, out var conversation) && conversation.IsOwnedBy(principal)
            ? conversation
            : null;
}
