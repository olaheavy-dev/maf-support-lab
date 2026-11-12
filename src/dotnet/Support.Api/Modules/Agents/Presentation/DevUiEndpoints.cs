using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;

namespace Support.Api.Modules.Agents.Presentation;

/// <summary>
/// Development only: DevUI and the OpenAI-compatible Responses endpoints it talks to, mapped only here.
/// DevUI lists only the <c>support-routing</c> workflow (registered by the Triage module), never the specialist agents
/// on their own: a direct agent chat would skip triage and the approval gate, so the model could propose a write the
/// API never would (an ineligible cancel, or a refund of another customer's order). Every DevUI chat takes the API's path.
/// </summary>
internal static class DevUiEndpoints
{
    public static WebApplicationBuilder AddDevUi(this WebApplicationBuilder builder)
    {
        builder.Services.AddOpenAIResponses();
        builder.Services.AddOpenAIConversations();
        builder.AddDevUI();
        return builder;
    }

    public static WebApplication MapDevUi(this WebApplication app)
    {
        app.MapOpenAIResponses();
        app.MapOpenAIConversations();
        app.MapDevUI();
        return app;
    }
}
