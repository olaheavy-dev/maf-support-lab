using System.Reflection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Support.Api.Common.Application.Observability;

namespace Support.Api.Common.Infrastructure.Observability;

/// <summary>
/// Subscribes the one OpenTelemetry pipeline (set up by Support.ServiceDefaults, which also owns the single OTLP
/// exporter) to the API's own spans and metrics, and adds version and environment to the service identity.
/// The service name comes from OTEL_SERVICE_NAME, which the Aspire AppHost sets ("support-api-dotnet").
/// </summary>
internal static class SupportTelemetryRegistration
{
    public static IHostApplicationBuilder AddSupportTelemetry(this IHostApplicationBuilder builder)
    {
        var version = typeof(SupportTelemetryRegistration).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddAttributes(
            [
                new("service.version", version),
                new("deployment.environment.name", builder.Environment.EnvironmentName),
            ]))
            .WithTracing(tracing => tracing.AddSource(SupportTelemetry.Name))
            .WithMetrics(metrics => metrics.AddMeter(SupportTelemetry.Name));
        return builder;
    }
}
