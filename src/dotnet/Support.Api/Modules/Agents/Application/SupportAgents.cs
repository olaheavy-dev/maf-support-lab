using Microsoft.Agents.AI;

namespace Support.Api.Modules.Agents.Application;

/// <summary>An agent a feature module plugs in. The Agents module never needs to know which modules exist.</summary>
public sealed record SupportAgent(string Name, AIAgent Agent);

/// <summary>
/// The application's agents, built once and shared by every entry point (contract API and DevUI).
/// Feature modules register a <see cref="SupportAgent"/> each; this class only collects them.
/// </summary>
public sealed class SupportAgents(IEnumerable<SupportAgent> agents)
{
    /// <summary>The orders agent's name: what DevUI lists and what a turn's route target reports.</summary>
    public const string OrdersAgentName = "orders";

    private readonly Dictionary<string, AIAgent> byName =
        agents.ToDictionary(agent => agent.Name, agent => agent.Agent, StringComparer.Ordinal);

    public IReadOnlyCollection<string> Names => byName.Keys;

    public AIAgent Orders => Get(OrdersAgentName);

    public AIAgent Get(string name) =>
        byName.TryGetValue(name, out var agent)
            ? agent
            : throw new InvalidOperationException($"No agent named '{name}' is registered.");
}
