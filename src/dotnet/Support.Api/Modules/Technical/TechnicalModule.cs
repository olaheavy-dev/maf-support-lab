using Microsoft.Extensions.AI;
using Support.Api.Common.Application.Messaging;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Presentation;
using Support.Api.Modules.Technical.Application;
using Support.Api.Modules.Technical.Application.CreateTicket;
using Support.Api.Modules.Technical.Application.SearchKnowledge;
using Support.Api.Modules.Technical.Domain;
using Support.Api.Modules.Technical.Infrastructure;
using Support.Api.Modules.Technical.Presentation;

namespace Support.Api.Modules.Technical;

/// <summary>Wires the Technical module: the knowledge base, its search tool, and the technical agent.</summary>
internal static class TechnicalModule
{
    public static IServiceCollection AddTechnicalModule(this IServiceCollection services)
    {
        services.AddSingleton<IKnowledgeBase>(_ => FixtureKnowledgeBase.Load());
        services.AddSingleton<IQueryHandler<SearchKnowledgeQuery, IReadOnlyList<KnowledgeArticle>>, SearchKnowledgeQueryHandler>();
        services.AddSingleton<KnowledgeSearchTool>();
        services.AddSingleton<ITicketStore, InMemoryTicketStore>();
        services.AddSingleton<IWriteHandler, CreateTicketWriteHandler>();
        services.AddSingleton(provider => new SupportAgent(
            SupportAgents.TechnicalAgentName,
            TechnicalAgentFactory.Create(provider.GetRequiredService<IChatClient>(), provider.GetRequiredService<KnowledgeSearchTool>(), provider.GetRequiredService<OperationTools>(), provider.GetService<ILoggerFactory>())));
        return services;
    }
}
