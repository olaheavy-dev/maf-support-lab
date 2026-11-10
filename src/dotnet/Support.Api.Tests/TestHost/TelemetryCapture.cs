using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Support.Api.Tests.TestHost;

/// <summary>
/// Adds in-memory exporters to the app's real OpenTelemetry pipeline, so tests see exactly what Aspire would receive.
/// Spans and metrics are process-wide (listeners subscribe by source name), so telemetry tests run in the
/// non-parallel "Telemetry" collection and select spans by trace id.
/// </summary>
internal sealed class TelemetryCapture
{
    public List<Activity> Spans { get; } = [];

    public List<MetricSnapshot> Metrics { get; } = [];

    public List<LogRecord> Logs { get; } = [];

    public void AddTo(IServiceCollection services) =>
        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddInMemoryExporter(Spans))
            .WithMetrics(metrics => metrics.AddInMemoryExporter(Metrics))
            .WithLogging(logging => logging.AddInMemoryExporter(Logs));

    /// <summary>Flushes metrics and waits for the trace's server span, which ends just after the response is sent.</summary>
    public async Task<IReadOnlyList<Activity>> TraceAsync(WebApplicationFactory<Program> factory, ActivityTraceId traceId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            factory.Services.GetRequiredService<TracerProvider>().ForceFlush();
            lock (Spans)
            {
                var spans = Spans.Where(span => span.TraceId == traceId).ToList();
                if (spans.Any(span => span.Kind == ActivityKind.Server))
                {
                    return spans;
                }
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No server span arrived for trace {traceId}.");
    }

    public IReadOnlyList<MetricSnapshot> FlushMetrics(WebApplicationFactory<Program> factory)
    {
        factory.Services.GetRequiredService<MeterProvider>().ForceFlush();
        return Metrics;
    }
}
