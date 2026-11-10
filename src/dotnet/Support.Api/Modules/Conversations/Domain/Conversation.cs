using System.Collections.Concurrent;
using Support.Api.Modules.Identity.Domain;

namespace Support.Api.Modules.Conversations.Domain;

/// <summary>
/// A conversation owned by one customer, with its finished turns kept for client_turn_id replay.
/// Per-conversation sessions, context, and the one-turn-at-a-time rule arrive in L09; durability in L11.
/// </summary>
public sealed class Conversation
{
    private readonly ConcurrentDictionary<string, TurnReservation> reservations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Turn> finishedTurns = new(StringComparer.Ordinal);

    private Conversation(string conversationId, string ownerCustomerId, DateTimeOffset createdAt)
    {
        ConversationId = conversationId;
        OwnerCustomerId = ownerCustomerId;
        CreatedAt = createdAt;
    }

    public string ConversationId { get; }

    public string OwnerCustomerId { get; }

    public DateTimeOffset CreatedAt { get; }

    public static Conversation Start(SupportPrincipal owner, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new Conversation(Ids.New("conv_"), owner.CustomerId, now);
    }

    public bool IsOwnedBy(SupportPrincipal principal) =>
        string.Equals(OwnerCustomerId, principal?.CustomerId, StringComparison.Ordinal);

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

    public void Complete(TurnReservation reservation, Turn turn)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(turn);
        finishedTurns[turn.TurnId] = turn;
        reservation.Completion.TrySetResult(turn);
    }

    /// <summary>Releases a reservation whose run failed, so the client can retry with the same id.</summary>
    public void Abandon(string clientTurnId, TurnReservation reservation, Exception error)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        reservations.TryRemove(KeyValuePair.Create(clientTurnId, reservation));
        reservation.Completion.TrySetException(error);
    }

    public Turn? FindTurn(string turnId) => finishedTurns.GetValueOrDefault(turnId);
}
