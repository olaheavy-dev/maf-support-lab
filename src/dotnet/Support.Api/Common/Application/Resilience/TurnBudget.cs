namespace Support.Api.Common.Application.Resilience;

/// <summary>
/// One turn's running budget: how many model calls it has made and, if the provider rate-limited it, the retry
/// hint it gave. It is set for the detached run of a turn and flows to every model call within it. Model calls made
/// outside a turn (DevUI, tests) are not budgeted here; they keep the per-call limits.
/// </summary>
public sealed class TurnBudget
{
    private static readonly AsyncLocal<TurnBudget?> Active = new();
    private int modelCalls;
    private int modelAttempts;
    private int retryAfterSeconds;

    private TurnBudget(int maxModelCalls) => MaxModelCalls = maxModelCalls;

    public static TurnBudget? Current => Active.Value;

    public int MaxModelCalls { get; }

    public int ModelCalls => Volatile.Read(ref modelCalls);

    /// <summary>HTTP attempts to the model, including the SDK's retries.</summary>
    public int ModelAttempts => Volatile.Read(ref modelAttempts);

    /// <summary>The provider's last retry-after hint, in seconds (0 if none).</summary>
    public int RetryAfterSeconds => Volatile.Read(ref retryAfterSeconds);

    public static IDisposable Begin(int maxModelCalls)
    {
        var previous = Active.Value;
        Active.Value = new TurnBudget(maxModelCalls);
        return new Restore(previous);
    }

    /// <summary>Counts one model call; throws <see cref="TurnBudgetExhaustedException"/> when the turn has used its budget.</summary>
    public void ConsumeModelCall()
    {
        if (Interlocked.Increment(ref modelCalls) > MaxModelCalls)
        {
            throw new TurnBudgetExhaustedException($"The turn used its budget of {MaxModelCalls} model calls.");
        }
    }

    public void CountAttempt() => Interlocked.Increment(ref modelAttempts);

    public void RecordRetryAfter(int seconds) => Volatile.Write(ref retryAfterSeconds, seconds);

    private sealed class Restore(TurnBudget? previous) : IDisposable
    {
        public void Dispose() => Active.Value = previous;
    }
}

/// <summary>A turn used up one of its budgets (model calls or tool steps). The turn ends failed/budget_exhausted.</summary>
public sealed class TurnBudgetExhaustedException : Exception
{
    public TurnBudgetExhaustedException()
    {
    }

    public TurnBudgetExhaustedException(string message)
        : base(message)
    {
    }

    public TurnBudgetExhaustedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A tool did not finish within its time limit. The turn ends failed/tool_unavailable.</summary>
public sealed class ToolTimeoutException : Exception
{
    public ToolTimeoutException()
    {
    }

    public ToolTimeoutException(string message)
        : base(message)
    {
    }

    public ToolTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
