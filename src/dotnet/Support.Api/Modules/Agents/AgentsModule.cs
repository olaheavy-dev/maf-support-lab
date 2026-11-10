using Microsoft.Extensions.Options;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Agents.Infrastructure.ModelProvider;
using Support.Api.Modules.Agents.Infrastructure.Observability;
using Support.Api.Modules.Agents.Presentation;

namespace Support.Api.Modules.Agents;

/// <summary>
/// Wires the Agents module: the model provider, the collection of agents, and Development-only DevUI.
/// It is the shared AI runtime and depends on no feature module; feature modules plug their agents in.
/// </summary>
internal static class AgentsModule
{
    public static WebApplicationBuilder AddAgentsModule(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<AgentTelemetryOptions>().Bind(builder.Configuration.GetSection(AgentTelemetryOptions.SectionName));
        builder.Services.AddAgentTelemetry();
        builder.Services.AddSingleton<ToolMetrics>();
        builder.Services.AddModelProvider(builder.Configuration);
        // Feature modules (Orders today) register their own SupportAgent; this only collects them.
        builder.Services.AddSingleton<SupportAgents>();

        if (builder.Environment.IsDevelopment())
        {
            builder.AddDevUiForAgents([.. SupportAgents.SpecialistNames]);
        }

        return builder;
    }

    public static WebApplication MapAgentsModule(this WebApplication app)
    {
        var modelMode = app.Services.GetRequiredService<IOptions<ModelProviderOptions>>().Value.Mode;
        ModelProviderLog.StartingInMode(app.Logger, modelMode);

        if (app.Environment.IsDevelopment())
        {
            app.MapDevUiForAgents();
        }

        return app;
    }
}
