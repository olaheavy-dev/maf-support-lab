using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Support.Api.Contract;
using Support.Api.Fixtures;
using Support.Api.Orders;

namespace Support.Api.Agents;

internal static class AgentRegistration
{
    public static IServiceCollection AddSupportAgents(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => FixtureCatalog.Load());
        services.AddSingleton<OrderService>();
        services.AddSingleton<OrderStatusTool>();
        services.AddSingleton<ConversationStore>();
        services.AddSingleton(provider => new SupportAgents(
            OrdersAgentFactory.Create(
                provider.GetRequiredService<IChatClient>(),
                provider.GetRequiredService<OrderStatusTool>(),
                provider.GetService<ILoggerFactory>())));
        return services;
    }

    /// <summary>
    /// Development only: lets DevUI discover the same orders agent instance the API uses.
    /// DevUI talks to it through the OpenAI-compatible Responses endpoints, which are mapped only here.
    /// </summary>
    public static WebApplicationBuilder AddDevelopmentAgentTools(this WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment())
        {
            return builder;
        }

        builder.Services.AddAIAgent(OrdersAgentFactory.AgentName, (provider, _) => provider.GetRequiredService<SupportAgents>().Orders);
        builder.Services.AddOpenAIResponses();
        builder.Services.AddOpenAIConversations();
        builder.AddDevUI();
        return builder;
    }

    public static WebApplication MapDevelopmentAgentTools(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenAIResponses();
            app.MapOpenAIConversations();
            app.MapDevUI();
        }

        return app;
    }
}
