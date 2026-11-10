using System.Diagnostics.Metrics;
using Support.Api.Common.Application.Observability;

namespace Support.Api.Modules.Triage.Application;

/// <summary>
/// Triage decisions by outcome, target, and unrouted reason: the raw material for spotting poor routing
/// (for example a rising share of ambiguous_request). All three labels come from small fixed sets.
/// </summary>
public sealed class TriageMetrics
{
    private readonly Counter<long> decisions;

    public TriageMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        decisions = meterFactory.Create(SupportTelemetry.Name)
            .CreateCounter<long>("support.triage.decisions", unit: "{decision}", description: "Triage decisions, by outcome, target, and unrouted reason.");
    }

    public void Record(string outcome, string target, string reason) =>
        decisions.Add(
            1,
            new KeyValuePair<string, object?>("support.triage.outcome", outcome),
            new KeyValuePair<string, object?>("support.triage.target", target),
            new KeyValuePair<string, object?>("support.triage.unrouted_reason", reason));
}
