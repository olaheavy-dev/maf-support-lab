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
    private readonly ConcurrentDictionary<string, TurnReservation> reservations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TurnResult> finishedTurns = new(StringComparer.Ordinal);

    public string ConversationId { get; } = conversationId;

    public string OwnerCustomerId { get; } = ownerCustomerId;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    /// <summary>
    /// Atomically claims a client_turn_id with its message <em>before</em> any work starts. Exactly one caller
    /// becomes the owner and runs the turn; every other caller with that id gets the same reservation and
    /// either waits for its result (same message) or is rejected (different message).
    /// </summary>
    public (TurnReservation Reservation, bool IsOwner) Reserve(string clientTurnId, string message)
    {
        var mine = new TurnReservation(message);
        var reservation = reservations.GetOrAdd(clientTurnId, mine);
        return (reservation, ReferenceEquals(reservation, mine));
    }

    public void Complete(TurnReservation reservation, TurnResult result)
    {
        finishedTurns[result.TurnId] = result;
        reservation.Completion.TrySetResult(result);
    }

    /// <summary>Releases a reservation whose run failed, so the client can retry with the same id.</summary>
    public void Abandon(string clientTurnId, TurnReservation reservation, Exception error)
    {
        reservations.TryRemove(KeyValuePair.Create(clientTurnId, reservation));
        reservation.Completion.TrySetException(error);
    }

    public TurnResult? FindByTurnId(string turnId) => finishedTurns.GetValueOrDefault(turnId);
}

public sealed class TurnReservation(string message)
{
    public string Message { get; } = message;

    public TaskCompletionSource<TurnResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
