using System.Diagnostics.Metrics;
using Support.Api.Common.Application.Observability;
using Support.Api.Modules.Operations.Domain;

namespace Support.Api.Modules.Operations.Application;

/// <summary>Write lifecycle counts by kind and outcome (awaiting_approval, rejected, expired, committed, failed, not_eligible …). Bounded labels only.</summary>
public sealed class OperationMetrics
{
    private readonly Counter<long> operations;

    public OperationMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        operations = meterFactory.Create(SupportTelemetry.Name)
            .CreateCounter<long>("support.operations", unit: "{event}", description: "Write proposals and outcomes, by kind and outcome.");
    }

    public void Record(OperationKind kind, string outcome) =>
        operations.Add(1, new KeyValuePair<string, object?>("support.operation.kind", OperationNames.Of(kind)), new KeyValuePair<string, object?>("support.operation.outcome", outcome));
}
