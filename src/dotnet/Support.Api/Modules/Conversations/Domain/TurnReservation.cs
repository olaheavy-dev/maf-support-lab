namespace Support.Api.Modules.Conversations.Domain;

/// <summary>
/// A claimed client_turn_id (or approval key): the turn id it will have, the message it was claimed with, the turn it
/// will produce, and, while it runs, what a status lookup or a cancel request needs (L12). Its cancellation belongs to
/// the turn, not to any HTTP request: only a cancel request triggers it, so a dropped connection never stops the work.
/// </summary>
public sealed class TurnReservation(string turnId, string message) : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private int cancelRequested;

    public string TurnId { get; } = turnId;

    public string Message { get; } = message;

    public TaskCompletionSource<Turn> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string? ClientTurnId { get; set; }

    public string? ResumedFromTurnId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public string TraceId { get; set; } = string.Empty;

    /// <summary>Signalled only by <see cref="RequestCancel"/>. Work checks it at its defined boundaries.</summary>
    public CancellationToken CancellationToken => cancellation.Token;

    public bool CancelRequested => Volatile.Read(ref cancelRequested) == 1;

    /// <summary>Asks the running work to stop at its next boundary. Cooperative: it may already be past the commit.</summary>
    public void RequestCancel()
    {
        Volatile.Write(ref cancelRequested, 1);
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The turn finished in the meantime: nothing left to stop.
        }
    }

    /// <summary>Called once the turn has finished or was released; its cancellation is no longer needed.</summary>
    public void Dispose() => cancellation.Dispose();
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
