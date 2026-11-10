using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Support.Api.Orders;

namespace Support.Api.Agents;

/// <summary>
/// Builds the orders agent once. The same instance serves the contract API and DevUI, so both
/// entry points use the same tool and therefore the same ownership checks.
/// </summary>
public static class OrdersAgentFactory
{
    public const string AgentName = "orders";

    public const string Instructions = """
        You are the orders specialist for VoltCart customer support.
        Use the get_order_status tool for any question about a specific order; never guess order details.
        If the customer has not given an order id, ask for it (format ORD-1234) instead of calling the tool.
        If the tool reports not_found, say you could not find that order on their account and ask them to check the id.
        Never ask for or mention customer ids, and never discuss other customers' orders.
        Keep replies short and factual.
        """;

    public static AIAgent Create(IChatClient chatClient, OrderStatusTool orderStatusTool, ILoggerFactory? loggerFactory = null)
    {
        return new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = AgentName,
                Description = "Answers order-status questions for the current customer.",
                ChatOptions = new ChatOptions
                {
                    Instructions = Instructions,
                    Tools = [orderStatusTool.AsAIFunction()],
                },
            },
            loggerFactory);
    }
}

/// <summary>The application's agents, built once and shared by every entry point.</summary>
public sealed class SupportAgents(AIAgent orders)
{
    public AIAgent Orders { get; } = orders;
}
