using Microsoft.Extensions.AI;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Triage.Application;
using Support.Api.Modules.Triage.Infrastructure;
using Support.Api.Modules.Triage.Infrastructure.Routing;

namespace Support.Api.Modules.Triage;

/// <summary>Wires the Triage module: the triage agent (plugged into Agents) and the routing workflow.</summary>
internal static class TriageModule
{
    public static IServiceCollection AddTriageModule(this IServiceCollection services)
    {
        services.AddSingleton<TriageMetrics>();
        services.AddSingleton(provider => new SupportAgent(
            TriageAgentFactory.AgentName,
            TriageAgentFactory.Create(provider.GetRequiredService<IChatClient>(), provider.GetService<ILoggerFactory>())));
        services.AddSingleton<ISupportRouter, SupportWorkflowRouter>();
        return services;
    }
}
