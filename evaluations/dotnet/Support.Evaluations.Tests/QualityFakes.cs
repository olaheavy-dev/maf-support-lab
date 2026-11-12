using Microsoft.Extensions.AI;
using Support.Evaluations.Quality;

namespace Support.Evaluations.Tests;

/// <summary>A judge that answers in the evaluators' tagged format with a score chosen from the prompt it was sent.</summary>
internal sealed class FakeJudge(Func<string, int> score, Func<string, string>? reply = null, TimeSpan? delay = null) : IChatClient
{
    private int calls;

    public int Calls => Volatile.Read(ref calls);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        if (delay is { } wait)
        {
            await Task.Delay(wait, cancellationToken);
        }

        var prompt = string.Join("\n", messages.Select(m => m.Text));
        var text = reply?.Invoke(prompt) ?? $"<S0>Let's think step by step.</S0><S1>Fake judge.</S1><S2>{score(prompt)}</S2>";
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20 }, ModelId = "fake-judge" };
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>Backends that answer every question with a grounded reply (or as told), counting and timing the asks.</summary>
internal sealed class FakeReplies(Func<string, BackendReply>? answer = null, TimeSpan? delay = null, Action<int>? onAsk = null) : IReplySource
{
    private int asked;
    private int active;
    private int maxConcurrent;
    private readonly Lock gate = new();

    public int Asked => Volatile.Read(ref asked);

    public int MaxConcurrent => Volatile.Read(ref maxConcurrent);

    public async Task<BackendReply> AskAsync(string backend, string customer, string message, CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref asked);
        var now = Interlocked.Increment(ref active);
        lock (gate)
        {
            maxConcurrent = Math.Max(maxConcurrent, now);
        }

        try
        {
            onAsk?.Invoke(count);
            if (delay is { } wait)
            {
                await Task.Delay(wait, CancellationToken.None);
            }

            return answer?.Invoke(message) ?? new BackendReply("completed", $"Here is what I found for: {message}", "orders", null);
        }
        finally
        {
            Interlocked.Decrement(ref active);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
