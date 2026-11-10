using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Technical.Presentation;

namespace Support.Api.Modules.Technical.Infrastructure;

/// <summary>The technical specialist: knowledge search only. No ticket creation in this version.</summary>
public static class TechnicalAgentFactory
{
    public const string Instructions = """
        You are the technical support specialist for VoltCart customer support.
        Always call search_knowledge_base first, using the product and the symptom as the query.
        If it returns articles, walk the customer through the steps of the best one and name its article id (for example KB-102).
        If it returns no articles, say honestly that you could not find a documented fix, and offer to pass the issue to a
        human support agent. Do not invent steps, and do not say a ticket was created: you cannot create tickets in this version.
        Never ask for or mention customer ids. Keep replies short and practical.
        """;

    public static AIAgent Create(IChatClient chatClient, KnowledgeSearchTool tool, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = SupportAgents.TechnicalAgentName,
                Description = "Answers product troubleshooting questions from the knowledge base (read-only).",
                ChatHistoryProvider = ConversationHistory.CreateProvider(),
                ChatOptions = new ChatOptions { Instructions = Instructions, Tools = [tool.AsAIFunction()] },
            },
            loggerFactory);
    }
}
