using System.Diagnostics.Metrics;
using Support.Api.Common.Application.Observability;

namespace Support.Api.Modules.Conversations.Application;

/// <summary>
/// Turn counts and latency. Labels are bounded: agent (one per specialist), outcome (completed, failed, replayed),
/// and reply kind (a short fixed list). Conversation and turn ids go on spans and logs, never here.
/// </summary>
public sealed class TurnMetrics
{
    private readonly Counter<long> turns;
    private readonly Histogram<double> duration;

    public TurnMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(SupportTelemetry.Name);
        turns = meter.CreateCounter<long>("support.turns", unit: "{turn}", description: "Turns handled, by agent, outcome, and reply kind.");
        duration = meter.CreateHistogram<double>("support.turn.duration", unit: "s", description: "Time to handle a turn, by agent and outcome.");
    }

    public void Record(string agent, string outcome, string? replyKind, TimeSpan elapsed)
    {
        KeyValuePair<string, object?>[] tags =
        [
            new("support.agent", agent),
            new("support.turn.outcome", outcome),
            new("support.reply_kind", replyKind ?? "none"),
        ];
        turns.Add(1, tags);
        duration.Record(elapsed.TotalSeconds, tags[0], tags[1]);
    }
}
