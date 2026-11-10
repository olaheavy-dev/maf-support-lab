using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using Support.Api.Modules.Agents.Application;

namespace Support.Api.Modules.Agents.Presentation;

/// <summary>
/// Development only: lets DevUI discover the same agent instances the API uses.
/// DevUI talks to them through the OpenAI-compatible Responses endpoints, which are mapped only here.
/// </summary>
internal static class DevUiEndpoints
{
    public static WebApplicationBuilder AddDevUiForAgents(this WebApplicationBuilder builder, params string[] agentNames)
    {
        foreach (var name in agentNames)
        {
            builder.Services.AddAIAgent(name, (provider, _) => provider.GetRequiredService<SupportAgents>().Get(name));
        }

        builder.Services.AddOpenAIResponses();
        builder.Services.AddOpenAIConversations();
        builder.AddDevUI();
        return builder;
    }

    public static WebApplication MapDevUiForAgents(this WebApplication app)
    {
        app.MapOpenAIResponses();
        app.MapOpenAIConversations();
        app.MapDevUI();
        return app;
    }
}
