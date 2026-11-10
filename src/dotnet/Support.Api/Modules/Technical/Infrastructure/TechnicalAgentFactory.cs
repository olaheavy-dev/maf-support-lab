using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Operations.Presentation;
using Support.Api.Modules.Technical.Presentation;

namespace Support.Api.Modules.Technical.Infrastructure;

/// <summary>The technical specialist: knowledge search only. No ticket creation in this version.</summary>
public static class TechnicalAgentFactory
{
    public const string Instructions = """
        You are the technical support specialist for VoltCart customer support.
        Always call search_knowledge_base first, using the product and the symptom as the query.
        If it returns articles, walk the customer through the steps of the best one and name its article id (for example KB-102).
        If it returns no articles, say honestly that you could not find a documented fix, and offer to open a support ticket.
        If the customer asks for a ticket, call create_ticket with a short subject and their description. The customer will
        be asked to approve it; never say a ticket exists until the tool reports "committed". Do not invent steps.
        Never ask for or mention customer ids. Keep replies short and practical.
        """;

    public static AIAgent Create(IChatClient chatClient, KnowledgeSearchTool tool, OperationTools writeTools, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Id = SupportAgents.TechnicalAgentName, // stable across restarts, so a stored session belongs to the same agent (L11)
                Name = SupportAgents.TechnicalAgentName,
                Description = "Answers troubleshooting questions from the knowledge base; tickets need the customer's approval.",
                ChatHistoryProvider = ConversationHistory.CreateProvider(),
                ChatOptions = new ChatOptions { Instructions = Instructions, Tools = [tool.AsAIFunction(), writeTools.CreateTicket] },
            },
            loggerFactory);
    }
}
