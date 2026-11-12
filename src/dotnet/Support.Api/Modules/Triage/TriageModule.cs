using Microsoft.Agents.AI.Hosting;
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
        services.AddSingleton<ConversationSessions>();
        services.AddSingleton<PendingNativeApprovals>();
        services.AddSingleton<ApprovalGate>();
        services.AddSingleton<SupportWorkflowRouter>();
        services.AddSingleton<ISupportRouter>(provider => provider.GetRequiredService<SupportWorkflowRouter>());
        return services;
    }

    /// <summary>
    /// Development only: lists the support workflow in DevUI (its only entity), so a DevUI chat goes through
    /// triage and the routing switch exactly like an API turn. Transient, so every DevUI run gets a fresh workflow.
    /// </summary>
    public static WebApplicationBuilder AddTriageDevUi(this WebApplicationBuilder builder)
    {
        if (builder.Environment.IsDevelopment())
        {
            builder.AddWorkflow(
                SupportWorkflowRouter.WorkflowName,
                (provider, _) => provider.GetRequiredService<SupportWorkflowRouter>().BuildForChat(),
                ServiceLifetime.Transient);
        }

        return builder;
    }
}
