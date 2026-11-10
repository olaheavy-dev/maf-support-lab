using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Agents.Infrastructure.ModelProvider;
using Support.Api.Modules.Billing.Presentation;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Orders.Presentation;
using Support.Api.Modules.Technical.Presentation;

namespace Support.Api.Tests.Modules.Agents;

/// <summary>
/// Each agent can do exactly what its job needs (L08), and every tool that can change data requires the customer's
/// approval through the framework's native gate (L10). The tool lists are read from the agents the app actually built.
/// </summary>
public sealed partial class AgentCapabilityTests
{
    [Theory]
    [InlineData("triage", new string[0])]
    [InlineData("orders", new[] { "cancel_order", "get_order_status" })]
    [InlineData("billing", new[] { "get_invoice", "get_payment_history", "refund_order" })]
    [InlineData("technical", new[] { "create_ticket", "search_knowledge_base" })]
    public void Each_agent_has_exactly_its_tools(string agent, string[] tools)
    {
        Assert.Equal(tools.Order(StringComparer.Ordinal), ToolsOf(agent));
    }

    [Fact]
    public void Every_tool_that_can_change_data_requires_native_approval_and_no_read_tool_does()
    {
        using var factory = SupportApiFactory.Create();
        var agents = factory.Services.GetRequiredService<SupportAgents>();

        var tools = agents.Names.SelectMany(name => ToolObjectsOf(agents, name)).ToList();
        var writes = tools.Where(tool => WriteVerb().IsMatch(tool.Name)).ToList();

        Assert.Equal(3, writes.Count);
        Assert.All(writes, tool => Assert.IsType<ApprovalRequiredAIFunction>(tool));
        Assert.All(tools.Except(writes), tool => Assert.IsNotType<ApprovalRequiredAIFunction>(tool));
    }

    [Fact]
    public void The_fake_model_uses_the_real_tool_names()
    {
        Assert.Equal(OrderStatusTool.Name, FakeChatClient.OrderStatusToolName);
        Assert.Equal(BillingTools.InvoiceToolName, FakeChatClient.InvoiceToolName);
        Assert.Equal(BillingTools.PaymentHistoryToolName, FakeChatClient.PaymentHistoryToolName);
        Assert.Equal(KnowledgeSearchTool.Name, FakeChatClient.KnowledgeSearchToolName);
        Assert.Equal(WriteTools.CancelOrder, FakeChatClient.CancelOrderToolName);
        Assert.Equal(WriteTools.RefundOrder, FakeChatClient.RefundOrderToolName);
        Assert.Equal(WriteTools.CreateTicket, FakeChatClient.CreateTicketToolName);
    }

    private static IReadOnlyList<string> ToolsOf(string agent)
    {
        using var factory = SupportApiFactory.Create();
        return [.. ToolObjectsOf(factory.Services.GetRequiredService<SupportAgents>(), agent).Select(tool => tool.Name).Order(StringComparer.Ordinal)];
    }

    private static IEnumerable<AIFunctionDeclaration> ToolObjectsOf(SupportAgents agents, string agent)
    {
        var options = agents.Get(agent).GetService<ChatClientAgentOptions>();
        Assert.NotNull(options);
        return (options.ChatOptions?.Tools ?? []).OfType<AIFunctionDeclaration>();
    }

    [GeneratedRegex("cancel|refund|create|update|delete|submit|set_|issue|charge|pay_", RegexOptions.IgnoreCase)]
    private static partial Regex WriteVerb();
}
