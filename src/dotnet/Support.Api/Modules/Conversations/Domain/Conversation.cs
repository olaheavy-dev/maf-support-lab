using Support.Api.Modules.Identity.Domain;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Conversations.Domain;

/// <summary>
/// A conversation owned by one customer: its finished turns (kept for client_turn_id replay), the one turn allowed to
/// run at a time, and the verified routing context that follow-ups use. In memory; durability arrives in L11.
/// </summary>
public sealed class Conversation
{
    private readonly Dictionary<string, TurnReservation> reservations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Turn> finishedTurns = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private TurnReservation? activeTurn;
    private ConversationContext context = ConversationContext.Empty;

    private Conversation(string conversationId, string ownerCustomerId, DateTimeOffset createdAt)
    {
        ConversationId = conversationId;
        OwnerCustomerId = ownerCustomerId;
        CreatedAt = createdAt;
    }

    public string ConversationId { get; }

    public string OwnerCustomerId { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>The turn running right now, if any (contract: active_turn_id).</summary>
    public string? ActiveTurnId
    {
        get
        {
            lock (gate)
            {
                return activeTurn?.TurnId;
            }
        }
    }

    /// <summary>Verified facts from earlier turns, for follow-ups.</summary>
    public ConversationContext Context
    {
        get
        {
            lock (gate)
            {
                return context;
            }
        }
    }

    public static Conversation Start(SupportPrincipal owner, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new Conversation(Ids.New("conv_"), owner.CustomerId, now);
    }

    public bool IsOwnedBy(SupportPrincipal principal) =>
        string.Equals(OwnerCustomerId, principal?.CustomerId, StringComparison.Ordinal);

    /// <summary>
    /// Claims a client_turn_id with its message <em>before</em> any work starts, atomically:
    /// a client_turn_id seen before is a duplicate (wait for or replay that one run); otherwise, if another turn is
    /// still running, the new one is rejected; otherwise the caller becomes the owner and its turn becomes active.
    /// One turn at a time keeps the conversation's agent sessions from being used by two runs at once.
    /// </summary>
    public (TurnReservation? Reservation, ReservationOutcome Outcome) Reserve(string clientTurnId, string message)
    {
        lock (gate)
        {
            if (reservations.TryGetValue(clientTurnId, out var existing))
            {
                return (existing, ReservationOutcome.Duplicate);
            }

            if (activeTurn is not null)
            {
                return (null, ReservationOutcome.Busy);
            }

            var mine = new TurnReservation(Ids.New("turn_"), message);
            reservations[clientTurnId] = mine;
            activeTurn = mine;
            return (mine, ReservationOutcome.Owner);
        }
    }

    /// <summary>Stores the finished turn, remembers what it verified, and frees the conversation for the next turn.</summary>
    public void Complete(TurnReservation reservation, Turn turn, SupportReply reply)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(turn);
        lock (gate)
        {
            finishedTurns[turn.TurnId] = turn;
            context = context.After(reply);
            if (ReferenceEquals(activeTurn, reservation))
            {
                activeTurn = null;
            }
        }

        reservation.Completion.TrySetResult(turn);
    }

    /// <summary>Releases a reservation whose run failed, so the client can retry with the same id.</summary>
    public void Abandon(string clientTurnId, TurnReservation reservation, Exception error)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        lock (gate)
        {
            if (reservations.TryGetValue(clientTurnId, out var current) && ReferenceEquals(current, reservation))
            {
                reservations.Remove(clientTurnId);
            }

            if (ReferenceEquals(activeTurn, reservation))
            {
                activeTurn = null;
            }
        }

        reservation.Completion.TrySetException(error);
    }

    public Turn? FindTurn(string turnId)
    {
        lock (gate)
        {
            return finishedTurns.GetValueOrDefault(turnId);
        }
    }
}
