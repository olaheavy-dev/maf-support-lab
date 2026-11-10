using System.Collections.Concurrent;
using System.Security.Cryptography;
using Support.Api.Identity;

namespace Support.Api.Contract;

/// <summary>
/// In-memory conversations owned by a principal, with finished turns kept for client_turn_id replay.
/// Per-conversation sessions, context, and the one-turn-at-a-time rule arrive in L09; durability in L11.
/// </summary>
public sealed class ConversationStore(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, StoredConversation> conversations = new(StringComparer.Ordinal);

    public StoredConversation Create(SupportPrincipal owner)
    {
        var conversation = new StoredConversation(NewId("conv_"), owner.CustomerId, clock.GetUtcNow());
        conversations[conversation.ConversationId] = conversation;
        return conversation;
    }

    /// <summary>Returns the conversation only for its owner; anyone else gets null (reported as not_found).</summary>
    public StoredConversation? FindOwned(string conversationId, SupportPrincipal principal) =>
        conversations.TryGetValue(conversationId, out var conversation) &&
        string.Equals(conversation.OwnerCustomerId, principal.CustomerId, StringComparison.Ordinal)
            ? conversation
            : null;

    public static string NewId(string prefix) =>
        prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();
}

public sealed class StoredConversation(string conversationId, string ownerCustomerId, DateTimeOffset createdAt)
{
    private readonly ConcurrentDictionary<string, StoredTurn> turnsByClientId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StoredTurn> turnsById = new(StringComparer.Ordinal);

    public string ConversationId { get; } = conversationId;

    public string OwnerCustomerId { get; } = ownerCustomerId;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    public StoredTurn? FindByClientTurnId(string clientTurnId) => turnsByClientId.GetValueOrDefault(clientTurnId);

    public StoredTurn? FindByTurnId(string turnId) => turnsById.GetValueOrDefault(turnId);

    /// <summary>Stores a finished turn; if the same client_turn_id raced in first, returns that turn instead.</summary>
    public StoredTurn Add(StoredTurn turn)
    {
        var stored = turnsByClientId.GetOrAdd(turn.ClientTurnId, turn);
        turnsById.TryAdd(stored.Result.TurnId, stored);
        return stored;
    }
}

public sealed record StoredTurn(string ClientTurnId, string Message, TurnResult Result);
