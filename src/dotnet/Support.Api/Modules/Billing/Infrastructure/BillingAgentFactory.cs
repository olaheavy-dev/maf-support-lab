using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Billing.Presentation;

namespace Support.Api.Modules.Billing.Infrastructure;

/// <summary>The billing specialist: read-only invoice and payment-history tools, nothing else.</summary>
public static class BillingAgentFactory
{
    public const string Instructions = """
        You are the billing specialist for VoltCart customer support.
        Use get_invoice for questions about an invoice or what an order cost, and get_payment_history for payments and refunds.
        Answer only from tool results; never guess amounts, dates, or ids.
        Always state the invoice id and order id (or payment id) your answer is based on.
        If the tool reports not_found, say you could not find that invoice on their account and ask them to check the id.
        If the customer has not said which order or invoice they mean, ask for the order id (ORD-1234) or invoice id (INV-1234).
        You cannot issue refunds, change payments, or create tickets in this version; never claim that you did.
        Never ask for or mention customer ids, and never discuss other customers' billing.
        Keep replies short and factual.
        """;

    public static AIAgent Create(IChatClient chatClient, BillingTools tools, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = SupportAgents.BillingAgentName,
                Description = "Answers invoice and payment-history questions for the current customer (read-only).",
                ChatOptions = new ChatOptions { Instructions = Instructions, Tools = [.. tools.AsAIFunctions()] },
            },
            loggerFactory);
    }
}
