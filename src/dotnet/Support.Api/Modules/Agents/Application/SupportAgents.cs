using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;

namespace Support.Api.Modules.Agents.Application;

/// <summary>An agent a feature module plugs in. The Agents module never needs to know which modules exist.</summary>
public sealed record SupportAgent(string Name, AIAgent Agent);

/// <summary>
/// The application's agents, built once and shared by every entry point (contract API and DevUI).
/// Feature modules register a <see cref="SupportAgent"/> each; this class collects them and gives every agent the
/// same agent-level telemetry (invoke_agent spans on <see cref="AgentTelemetry.AgentSourceName"/>), so a new
/// specialist cannot forget it.
/// </summary>
public sealed class SupportAgents
{
    /// <summary>The orders agent's name: what DevUI lists and what a turn's route target reports.</summary>
    public const string OrdersAgentName = "orders";

    public const string BillingAgentName = "billing";

    public const string TechnicalAgentName = "technical";

    /// <summary>The specialists a turn can be routed to. DevUI lists these too.</summary>
    public static readonly IReadOnlyList<string> SpecialistNames = [OrdersAgentName, BillingAgentName, TechnicalAgentName];

    private readonly Dictionary<string, AIAgent> byName;

    public SupportAgents(IEnumerable<SupportAgent> agents, IOptions<AgentTelemetryOptions> telemetry)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(telemetry);
        var captureSensitiveData = telemetry.Value.CaptureSensitiveData;
        byName = agents.ToDictionary(
            agent => agent.Name,
            agent => agent.Agent
                .AsBuilder()
                .UseOpenTelemetry(AgentTelemetry.AgentSourceName, otel => otel.EnableSensitiveData = captureSensitiveData)
                .Build(),
            StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> Names => byName.Keys;

    public AIAgent Orders => Get(OrdersAgentName);

    public AIAgent Get(string name) =>
        byName.TryGetValue(name, out var agent)
            ? agent
            : throw new InvalidOperationException($"No agent named '{name}' is registered.");
}
