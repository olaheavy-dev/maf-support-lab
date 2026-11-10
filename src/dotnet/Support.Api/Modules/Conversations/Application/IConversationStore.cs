using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Identity.Domain;

namespace Support.Api.Modules.Conversations.Application;

/// <summary>Where conversations are kept. In memory today; durable storage arrives in L11 behind this same interface.</summary>
public interface IConversationStore
{
    void Add(Conversation conversation);

    /// <summary>Returns the conversation only for its owner; anyone else gets null (reported as not_found).</summary>
    Conversation? FindOwned(string conversationId, SupportPrincipal principal);
}
