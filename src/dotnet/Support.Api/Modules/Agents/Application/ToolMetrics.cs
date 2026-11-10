using System.Diagnostics.Metrics;
using Support.Api.Common.Application.Observability;

namespace Support.Api.Modules.Agents.Application;

/// <summary>Counts tool calls by tool and outcome. Both labels come from small fixed sets; ids never become labels.</summary>
public sealed class ToolMetrics
{
    private readonly Counter<long> calls;

    public ToolMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(SupportTelemetry.Name);
        calls = meter.CreateCounter<long>("support.tool.calls", unit: "{call}", description: "Tool calls made by agents, by tool and outcome.");
    }

    public void Record(string tool, string outcome) =>
        calls.Add(1, new KeyValuePair<string, object?>("support.tool.name", tool), new KeyValuePair<string, object?>("support.tool.outcome", outcome));
}
