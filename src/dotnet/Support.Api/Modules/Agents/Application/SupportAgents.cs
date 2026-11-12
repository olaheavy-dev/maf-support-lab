using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Support.Api.Common.Application.Resilience;

namespace Support.Api.Modules.Agents.Application;

/// <summary>An agent a feature module plugs in. The Agents module never needs to know which modules exist.</summary>
public sealed record SupportAgent(string Name, AIAgent Agent);

/// <summary>
/// The application's agents, built once and shared by every entry point (the contract API and the DevUI workflow).
/// Feature modules register a <see cref="SupportAgent"/> each; this class collects them and gives every agent the
/// same agent-level telemetry (invoke_agent spans on <see cref="AgentTelemetry.AgentSourceName"/>), so a new
/// specialist cannot forget it.
/// </summary>
public sealed class SupportAgents
{
    /// <summary>The orders agent's name: what a turn's route target reports.</summary>
    public const string OrdersAgentName = "orders";

    public const string BillingAgentName = "billing";

    public const string TechnicalAgentName = "technical";

    private readonly Dictionary<string, AIAgent> byName;

    public SupportAgents(IEnumerable<SupportAgent> agents, IOptions<AgentTelemetryOptions> telemetry, IOptions<ResilienceOptions> resilience)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(resilience);
        agents = [.. agents];
        foreach (var agent in agents)
        {
            Bound(agent.Agent, resilience.Value);
        }

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

    /// <summary>
    /// Bounds an agent's tool loop: MAF's function-invocation step limit, and a time limit per tool call. A tool
    /// that overruns or throws ends the turn (tool_unavailable) instead of letting the model retry it in a loop.
    /// </summary>
    private static void Bound(AIAgent agent, ResilienceOptions limits)
    {
        if (agent.GetService<FunctionInvokingChatClient>() is not { } loop)
        {
            return; // an agent without tools (triage) has no tool loop
        }

        loop.MaximumIterationsPerRequest = limits.MaxToolIterations;
        loop.MaximumConsecutiveErrorsPerRequest = 0;
        var toolTimeout = TimeSpan.FromSeconds(limits.ToolTimeoutSeconds);
        loop.FunctionInvoker = async (context, cancellationToken) =>
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(toolTimeout);
            try
            {
                return await context.Function.InvokeAsync(context.Arguments, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ToolTimeoutException($"Tool '{context.Function.Name}' did not finish within {toolTimeout.TotalSeconds:0} seconds.");
            }
        };
    }
}
