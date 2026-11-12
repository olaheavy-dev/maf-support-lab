using Support.Api.Common.Application.Resilience;
using Support.Api.Modules.Conversations.Domain;

namespace Support.Api.Modules.Conversations.Application;

/// <summary>
/// Maps a turn's failure to the contract's turn_error. The detail is fixed, user-safe text:
/// never the exception message, which may hold provider payloads or internals. The full exception goes to the logs and
/// the trace instead. Transient faults were already retried by their one retry owner (the provider SDK) before they
/// get here; nothing in the application retries a turn.
/// </summary>
internal static class TurnErrors
{
    public static TurnError From(Exception error, bool turnTimedOut = false, TurnBudget? budget = null)
    {
        if (turnTimedOut)
        {
            return new TurnError("timeout", "This took too long, so it was stopped. Please try again.");
        }

        var chain = Chain(error).ToList();
        if (chain.OfType<TurnBudgetExhaustedException>().Any())
        {
            return new TurnError("budget_exhausted", "This request needed more steps than allowed, so it was stopped. Please rephrase or try again.");
        }

        if (chain.OfType<ToolTimeoutException>().Any())
        {
            return new TurnError("tool_unavailable", "A lookup did not respond in time. Please try again shortly.");
        }

        if (chain.Any(e => Named(e, "AnthropicRateLimitException")))
        {
            return new TurnError("rate_limited", "The assistant is busy right now. Please try again shortly.", budget?.RetryAfterSeconds is > 0 and var seconds ? seconds : null);
        }

        if (chain.Any(e => e is TimeoutException))
        {
            return new TurnError("timeout", "The assistant took too long to answer. Please try again.");
        }

        // Provider faults the SDK already retried (5xx, overloaded, connection) mean the provider is unavailable. Other
        // provider errors (400, 401, 403, 404, 422) are permanent: retrying cannot help, so they are internal errors.
        if (chain.Any(e => Named(e, "Anthropic5xxException") || Named(e, "AnthropicOverloadedException") || e is HttpRequestException))
        {
            return new TurnError("model_unavailable", "The assistant is temporarily unavailable. Please try again shortly.");
        }

        return new TurnError("internal_error", "Something went wrong while handling your message. Please try again.");
    }

    private static bool Named(Exception error, string typeName) =>
        error.GetType().Name == typeName && error.GetType().Namespace?.StartsWith("Anthropic", StringComparison.Ordinal) == true;

    private static IEnumerable<Exception> Chain(Exception? error)
    {
        while (error is not null)
        {
            yield return error;
            if (error is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Chain))
                {
                    yield return inner;
                }

                yield break;
            }

            error = error.InnerException;
        }
    }
}
