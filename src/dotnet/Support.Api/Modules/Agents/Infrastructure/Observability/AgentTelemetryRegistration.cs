using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Support.Api.Modules.Agents.Application;

namespace Support.Api.Modules.Agents.Infrastructure.Observability;

/// <summary>Subscribes the shared OpenTelemetry pipeline to the agent and model layers.</summary>
internal static class AgentTelemetryRegistration
{
    /// <summary>The GenAI semantic-convention histogram for token counts.</summary>
    public const string TokenUsageInstrument = "gen_ai.client.token.usage";

    public static IServiceCollection AddAgentTelemetry(this IServiceCollection services)
    {
        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(AgentTelemetry.AgentSourceName, AgentTelemetry.ModelSourceName, AgentTelemetry.WorkflowSourceName))
            .WithMetrics(metrics => metrics
                .AddMeter(AgentTelemetry.AgentSourceName, AgentTelemetry.ModelSourceName, AgentTelemetry.WorkflowSourceName)
                // The agent layer re-records the whole run's token usage, which the model layer already recorded
                // call by call. Keep the per-call figures (the provider bills per call) and drop the agent copy, so a
                // dashboard summing token usage counts every token once.
                .AddView(instrument =>
                    instrument.Meter.Name == AgentTelemetry.AgentSourceName && instrument.Name == TokenUsageInstrument
                        ? MetricStreamConfiguration.Drop
                        : null));
        return services;
    }
}
