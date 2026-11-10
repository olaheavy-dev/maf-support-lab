using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Support.Api.Common.Application.Resilience;

namespace Support.Api.Modules.Agents.Infrastructure.ModelProvider;

/// <summary>
/// Counts every model call against the running turn's budget before it is made (L15). A model that loops (calling
/// tools forever, or asking for approvals again and again) runs out of budget and the turn fails safely instead of
/// spending without limit. Outermost in the pipeline, so a refused call never reaches the provider or its telemetry.
/// </summary>
internal sealed class BudgetedChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        TurnBudget.Current?.ConsumeModelCall();
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        TurnBudget.Current?.ConsumeModelCall();
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }
}

/// <summary>
/// Sits under the Anthropic SDK's own HTTP client (L15): counts every HTTP attempt (so retries are visible per turn)
/// and keeps the provider's retry-after hint, which the SDK honours when retrying but does not expose on its exception.
/// It never retries anything itself: the SDK is the single retry owner for model calls.
/// </summary>
internal sealed class ProviderAttemptHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        TurnBudget.Current?.CountAttempt();
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode == 429 && response.Headers.RetryAfter is { } retryAfter)
        {
            var seconds = retryAfter.Delta?.TotalSeconds ?? (retryAfter.Date - DateTimeOffset.UtcNow)?.TotalSeconds ?? 0;
            TurnBudget.Current?.RecordRetryAfter(Math.Max(1, (int)Math.Ceiling(seconds)));
        }

        return response;
    }
}
