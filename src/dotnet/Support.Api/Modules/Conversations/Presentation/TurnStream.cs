using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Support.Api.Common.Application.Streaming;
using Support.Api.Common.Domain;
using Support.Api.Common.Presentation;
using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Operations.Application;

namespace Support.Api.Modules.Conversations.Presentation;

/// <summary>
/// Server-Sent Events for a turn (contract 1.1 streaming). The turn's live events arrive through an
/// <see cref="ITurnObserver"/>; this class writes them as <c>turn.started</c>, <c>turn.progress</c>, <c>reply.delta</c>,
/// and one terminal <c>turn.final</c> carrying the stored turn result. Errors found before the turn starts are normal
/// problem responses. Reply text is the model's tokens as they arrive; if the reply the server settles on differs from
/// what was streamed (an approval request, a declined write, a failure), a <c>reply.reset</c> tells the client to
/// discard it and the final text follows. A client that disconnects simply stops receiving: the turn runs on.
/// </summary>
internal static class TurnStream
{
    /// <summary>The contract's limit for one reply.delta.</summary>
    public const int MaxDeltaLength = 2000;

    public static bool IsRequested(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Headers.Accept.Any(value => value?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>Runs the turn with a streaming observer and returns either a problem (before start) or the event stream.</summary>
    public static async Task<IResult> RunAsync(Func<ITurnObserver, Task<Result<Turn>>> run, IOperationStore operations, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var channel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
        var observer = new ChannelObserver(channel.Writer);
        var running = run(observer);
        _ = running.ContinueWith(_ => channel.Writer.TryComplete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        // A turn that never started (not found, busy, reused id, decision errors) is an ordinary HTTP response.
        if (!await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var result = await running.ConfigureAwait(false);
            return result.IsFailure ? ApiResults.Problem(result.Error) : ApiResults.Ok(TurnResult.From(result.Value, operations));
        }

        return TypedResults.ServerSentEvents(EventsAsync(channel.Reader, running, operations, clock, cancellationToken));
    }

    private static async IAsyncEnumerable<SseItem<string>> EventsAsync(
        ChannelReader<object> reader, Task<Result<Turn>> running, IOperationStore operations, TimeProvider clock, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long sequence = 0;
        var streamed = new StringBuilder();
        TurnStarted? started = null;

        await foreach (var item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (item)
            {
                case TurnStarted start:
                    started = start;
                    yield return Event(start, ++sequence, "turn.started", new StartedData(start.ClientTurnId, start.ResumedFromTurnId));
                    break;
                case TurnProgress progress when started is not null:
                    yield return Event(started, ++sequence, "turn.progress", ProgressData.From(progress));
                    break;
                case string text when started is not null:
                    streamed.Append(text);
                    foreach (var piece in Pieces(text))
                    {
                        yield return Event(started, ++sequence, "reply.delta", new DeltaData(piece));
                    }

                    break;
            }
        }

        if (started is null)
        {
            yield break;
        }

        var result = await FinalAsync(started, running, operations, clock).ConfigureAwait(false);

        // The streamed text must add up to the final reply: if it doesn't, reset, then send the reply itself.
        var final = result.Reply?.Text ?? string.Empty;
        if (!string.Equals(streamed.ToString(), final, StringComparison.Ordinal))
        {
            if (streamed.Length > 0)
            {
                yield return Event(started, ++sequence, "reply.reset", new ResetData());
            }

            foreach (var piece in Pieces(final))
            {
                yield return Event(started, ++sequence, "reply.delta", new DeltaData(piece));
            }
        }

        yield return Event(started, ++sequence, "turn.final", new FinalData(result));
    }

    /// <summary>The stored turn; if it could not be stored, a failed result (never shown as success).</summary>
    private static async Task<TurnResult> FinalAsync(TurnStarted started, Task<Result<Turn>> running, IOperationStore operations, TimeProvider clock)
    {
        try
        {
            var result = await running.ConfigureAwait(false);
            if (result.IsSuccess)
            {
                return TurnResult.From(result.Value, operations);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Fall through to a failed result: the stream has started, so failures end with turn.final (contract).
        }

        var now = clock.GetUtcNow();
        return new TurnResult(
            ContractVersion.Current, started.ConversationId, started.TurnId, started.ClientTurnId, "finished", "failed", null, null, [],
            Common.Application.TraceIds.Current(), now, now)
        {
            ResumedFromTurnId = started.ResumedFromTurnId,
            Error = new TurnErrorResult("internal_error", "Something went wrong while handling your message. Please try again.", null),
        };
    }

    /// <summary>
    /// Splits text into pieces of at most <see cref="MaxDeltaLength"/> UTF-16 units, never between the two halves of a
    /// surrogate pair (an emoji, for example), which would corrupt it (Codex review on PR #78).
    /// </summary>
    internal static IEnumerable<string> Pieces(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(MaxDeltaLength, text.Length - start);
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]))
            {
                length--;
            }

            yield return text.Substring(start, length);
            start += length;
        }
    }

    private static SseItem<string> Event(TurnStarted started, long sequence, string type, object data) =>
        new(JsonSerializer.Serialize(new StreamEvent(ContractVersion.Current, type, started.ConversationId, started.TurnId, sequence, data), ContractJson.Options), type)
        {
            EventId = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    /// <summary>Queues the turn's events for the response writer. Never blocks the turn, and never throws.</summary>
    private sealed class ChannelObserver(ChannelWriter<object> writer) : ITurnObserver
    {
        public void Started(TurnStarted started) => writer.TryWrite(started);

        public void Progress(TurnProgress progress) => writer.TryWrite(progress);

        public void Delta(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                writer.TryWrite(text);
            }
        }
    }

    private sealed record StreamEvent(string ContractVersion, string Type, string ConversationId, string TurnId, long Sequence, object Data);

    private sealed record StartedData(
        string? ClientTurnId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResumedFromTurnId);

    private sealed record ProgressData(
        string Stage,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RouteResult? Route,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tool)
    {
        public static ProgressData From(TurnProgress progress) => new(
            progress.Stage switch
            {
                TurnStage.Triage => "triage",
                TurnStage.Routed => "routed",
                TurnStage.Specialist => "specialist",
                TurnStage.ToolCall => "tool_call",
                TurnStage.AwaitingApproval => "awaiting_approval",
                _ => "executing_operation",
            },
            progress.RouteTarget is { } target ? new RouteResult("routed", target)
                : progress.UnroutedReason is { } reason ? new RouteResult("unrouted", null, reason)
                : null,
            progress.Tool);
    }

    private sealed record DeltaData(string Text);

    private sealed record ResetData;

    private sealed record FinalData(TurnResult Result);
}
