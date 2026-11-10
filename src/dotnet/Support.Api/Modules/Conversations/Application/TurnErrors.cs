using Support.Api.Modules.Conversations.Domain;

namespace Support.Api.Modules.Conversations.Application;

/// <summary>
/// Maps a turn's failure to the contract's turn_error (L12). The detail is fixed, user-safe text: never the exception
/// message, which may hold provider payloads or internals. The full exception goes to the logs and the trace instead.
/// </summary>
internal static class TurnErrors
{
    public static TurnError From(Exception error)
    {
        var cause = Innermost(error);
        return cause switch
        {
            TimeoutException or TaskCanceledException => new TurnError("timeout", "The assistant took too long to answer. Please try again."),
            HttpRequestException => new TurnError("model_unavailable", "The assistant is temporarily unavailable. Please try again shortly."),
            _ when cause.GetType().Namespace?.StartsWith("Anthropic", StringComparison.Ordinal) == true =>
                new TurnError("model_unavailable", "The assistant is temporarily unavailable. Please try again shortly."),
            _ => new TurnError("internal_error", "Something went wrong while handling your message. Please try again."),
        };
    }

    private static Exception Innermost(Exception error)
    {
        while (error is AggregateException or InvalidOperationException && error.InnerException is { } inner)
        {
            error = inner;
        }

        return error;
    }
}
