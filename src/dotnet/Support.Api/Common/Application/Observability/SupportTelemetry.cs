using System.Diagnostics;

namespace Support.Api.Common.Application.Observability;

/// <summary>
/// The API's own telemetry name: one ActivitySource (spans) and one Meter (metrics), both called "Support.Api".
/// Framework signals use their own names (see docs/observability.md). Metric instruments are created through
/// IMeterFactory in each module's metrics class; spans start from <see cref="ActivitySource"/>.
/// </summary>
public static class SupportTelemetry
{
    public const string Name = "Support.Api";

    public static readonly ActivitySource ActivitySource = new(Name);
}
