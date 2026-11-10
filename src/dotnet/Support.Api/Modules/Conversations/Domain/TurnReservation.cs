namespace Support.Api.Modules.Conversations.Domain;

/// <summary>A claimed client_turn_id: the turn id it will have, the message it was claimed with, and the turn it will produce.</summary>
public sealed class TurnReservation(string turnId, string message)
{
    public string TurnId { get; } = turnId;

    public string Message { get; } = message;

    public TaskCompletionSource<Turn> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>What happened when a turn was submitted to a conversation.</summary>
public enum ReservationOutcome
{
    /// <summary>This caller runs the turn.</summary>
    Owner,

    /// <summary>The same client_turn_id was already submitted: wait for (or replay) that one run.</summary>
    Duplicate,

    /// <summary>A different turn is still running in this conversation: rejected (turn_in_progress).</summary>
    Busy,
}
