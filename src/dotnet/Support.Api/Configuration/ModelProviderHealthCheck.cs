using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Support.Api.Configuration;

/// <summary>Reports the active model mode. It never calls the provider and never reports secrets.</summary>
internal sealed class ModelProviderHealthCheck(IOptions<ModelProviderOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var mode = options.Value.Mode;
        var data = new Dictionary<string, object> { ["mode"] = mode.ToString() };
        return Task.FromResult(HealthCheckResult.Healthy($"Model provider mode: {mode}", data));
    }
}
