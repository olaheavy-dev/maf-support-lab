using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Operations.Presentation;
using Support.Api.Modules.Orders.Presentation;

namespace Support.Api.Modules.Orders.Infrastructure;

/// <summary>
/// Builds the orders agent once. The same instance serves the contract API and DevUI, so both
/// entry points use the same tool and therefore the same ownership checks.
/// </summary>
public static class OrdersAgentFactory
{
    public const string Instructions = """
        You are the orders specialist for VoltCart customer support.
        Use the get_order_status tool for any question about a specific order; never guess order details.
        If the customer has not given an order id, ask for it (format ORD-1234) instead of calling the tool.
        If the tool reports not_found, say you could not find that order on their account and ask them to check the id.
        If the customer asks to cancel an order, call cancel_order with its id. The customer will be asked to approve the
        exact action; never claim it is done until the tool reports "committed". If a request is declined, explain why and
        do not ask again.
        Never ask for or mention customer ids, and never discuss other customers' orders.
        Keep replies short and factual.
        """;

    public static AIAgent Create(IChatClient chatClient, OrderStatusTool orderStatusTool, OperationTools writeTools, ILoggerFactory? loggerFactory = null)
    {
        return new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Id = SupportAgents.OrdersAgentName, // stable across restarts, so a stored session belongs to the same agent (L11)
                Name = SupportAgents.OrdersAgentName,
                Description = "Answers order-status questions for the current customer.",
                ChatHistoryProvider = ConversationHistory.CreateProvider(),
                ChatOptions = new ChatOptions
                {
                    Instructions = Instructions,
                    Tools = [orderStatusTool.AsAIFunction(), writeTools.CancelOrder],
                },
            },
            loggerFactory);
    }
}
