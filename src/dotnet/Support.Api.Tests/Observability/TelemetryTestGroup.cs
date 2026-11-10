namespace Support.Api.Tests.Observability;

/// <summary>
/// OpenTelemetry listeners are process-wide: a test's exporters would also receive other tests' spans and metrics.
/// Telemetry tests therefore run in this collection, alone, after the parallel tests finish.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryTestGroup
{
    public const string Name = "Telemetry";
}
