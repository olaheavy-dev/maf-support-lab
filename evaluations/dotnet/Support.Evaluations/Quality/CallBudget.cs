using Microsoft.Extensions.AI;

namespace Support.Evaluations.Quality;

/// <summary>
/// The lane's model-call budget. Work is admitted by reserving its worst case up front: a backend turn reserves
/// the backend's per-turn cap (the contract doesn't report how many calls a turn really made), a judge metric
/// reserves one call. Admission happens in plan order, so which samples a small budget skips is deterministic.
/// </summary>
internal sealed class CallBudget(int limit)
{
    private int reserved;

    public int Limit => limit;

    public int Reserved => Volatile.Read(ref reserved);

    /// <summary>Reserves <paramref name="calls"/> if they fit; otherwise reserves nothing and returns false.</summary>
    public bool TryReserve(int calls)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(calls);
        while (true)
        {
            var current = Volatile.Read(ref reserved);
            if (current + calls > limit)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref reserved, current + calls, current) == current)
            {
                return true;
            }
        }
    }
}

/// <summary>A judge call that no reservation covers: a lane bug, so it is refused instead of silently billed.</summary>
internal sealed class JudgeBudgetExceededException(int permits) : InvalidOperationException($"A judge call was made without a reserved permit ({permits} permits were granted).");

/// <summary>
/// The judge client wrapper: every call must be covered by a permit the lane granted (one per reserved judge call),
/// and every response's token usage is added up. Usage is "available" only if the provider reported it.
/// </summary>
internal sealed class BudgetedJudgeClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private int permits;
    private int calls;
    private int maxConcurrent;
    private int active;
    private long inputTokens;
    private long outputTokens;
    private int callsWithUsage;

    public int Calls => Volatile.Read(ref calls);

    public int MaxConcurrent => Volatile.Read(ref maxConcurrent);

    public bool UsageAvailable => Calls > 0 && Volatile.Read(ref callsWithUsage) == Calls;

    public long InputTokens => Interlocked.Read(ref inputTokens);

    public long OutputTokens => Interlocked.Read(ref outputTokens);

    /// <summary>Allows one more call (granted after the lane reserved it in the <see cref="CallBudget"/>).</summary>
    public void Grant() => Interlocked.Increment(ref permits);

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref calls);
        if (call > Volatile.Read(ref permits))
        {
            Interlocked.Decrement(ref calls);
            throw new JudgeBudgetExceededException(Volatile.Read(ref permits));
        }

        var now = Interlocked.Increment(ref active);
        for (var seen = Volatile.Read(ref maxConcurrent); now > seen && Interlocked.CompareExchange(ref maxConcurrent, now, seen) != seen; seen = Volatile.Read(ref maxConcurrent))
        {
        }

        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            if (response.Usage is { } usage && (usage.InputTokenCount is not null || usage.OutputTokenCount is not null))
            {
                Interlocked.Add(ref inputTokens, usage.InputTokenCount ?? 0);
                Interlocked.Add(ref outputTokens, usage.OutputTokenCount ?? 0);
                Interlocked.Increment(ref callsWithUsage);
            }

            return response;
        }
        finally
        {
            Interlocked.Decrement(ref active);
        }
    }

    /// <summary>The evaluators use GetResponseAsync; streaming would bypass the usage count, so it is refused.</summary>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The judge client counts calls and usage per response; streaming is not used.");
}
