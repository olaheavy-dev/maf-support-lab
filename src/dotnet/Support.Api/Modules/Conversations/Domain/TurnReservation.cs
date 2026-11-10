namespace Support.Api.Modules.Conversations.Domain;

/// <summary>A claimed client_turn_id: the message it was claimed with, and the turn it will produce.</summary>
public sealed class TurnReservation(string message)
{
    public string Message { get; } = message;

    public TaskCompletionSource<Turn> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
