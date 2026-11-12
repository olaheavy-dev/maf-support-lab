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
    /// <summary>
    /// The spans of one trace, once its server span and every span named in <paramref name="required"/> have ended.
    /// A turn runs detached from its request (L12), so its own spans (support.turn) can end a moment after the HTTP
    /// response: a test that reads them must name them, or it races the turn (seen as a CI flake in PR #113).
    /// </summary>
    public async Task<IReadOnlyList<Activity>> TraceAsync(WebApplicationFactory<Program> factory, ActivityTraceId traceId, params string[] required)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            factory.Services.GetRequiredService<TracerProvider>().ForceFlush();
            lock (Spans)
            {
                var spans = Spans.Where(span => span.TraceId == traceId).ToList();
                if (spans.Any(span => span.Kind == ActivityKind.Server) && required.All(name => spans.Any(span => span.DisplayName == name)))
                {
                    return spans;
                }
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"The server span{(required.Length > 0 ? " or one of " + string.Join(", ", required) : "")} did not arrive for trace {traceId}.");
    }

    public IReadOnlyList<MetricSnapshot> FlushMetrics(WebApplicationFactory<Program> factory)
    {
        factory.Services.GetRequiredService<MeterProvider>().ForceFlush();
        return Metrics;
    }
}
