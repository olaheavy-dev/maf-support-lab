using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Identity.Domain;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Conversations.Application;

/// <summary>Where conversations are kept: durably since L11, so a restart loses no finished turn or verified context.</summary>
public interface IConversationStore
{
    void Add(Conversation conversation);

    /// <summary>Returns the conversation only for its owner; anyone else gets null (reported as not_found).</summary>
    Conversation? FindOwned(string conversationId, SupportPrincipal principal);

    /// <summary>
    /// Durably records a finished turn under the key it was claimed with (a client_turn_id, or approval:{id}), and the
    /// conversation's context after it. Called before the turn is completed in memory, so nobody sees a turn that was not
    /// stored. The first turn stored for a key wins: if another process already stored one (e.g. a host that was stuck
    /// mid-turn while a restarted host answered the retry), that stored turn is returned and nothing is overwritten.
    /// </summary>
    Turn SaveTurn(string conversationId, string clientKey, string message, Turn turn, ConversationContext contextAfter);
}
