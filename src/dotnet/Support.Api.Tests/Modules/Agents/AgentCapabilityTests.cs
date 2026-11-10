using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Agents.Infrastructure.ModelProvider;
using Support.Api.Modules.Billing.Presentation;
using Support.Api.Modules.Orders.Presentation;
using Support.Api.Modules.Technical.Presentation;

namespace Support.Api.Tests.Modules.Agents;

/// <summary>
/// L08: each agent can do exactly what its job needs, and nothing in this build can change data. The tool lists are
/// read from the agents the application actually built, not from the factories' source.
/// </summary>
public sealed partial class AgentCapabilityTests
{
    [Theory]
    [InlineData("triage", new string[0])]
    [InlineData("orders", new[] { "get_order_status" })]
    [InlineData("billing", new[] { "get_invoice", "get_payment_history" })]
    [InlineData("technical", new[] { "search_knowledge_base" })]
    public void Each_agent_has_exactly_its_read_tools(string agent, string[] tools)
    {
        Assert.Equal(tools.Order(StringComparer.Ordinal), ToolsOf(agent));
    }

    [Fact]
    public void No_agent_has_a_tool_that_could_change_data()
    {
        using var factory = SupportApiFactory.Create();
        var agents = factory.Services.GetRequiredService<SupportAgents>();

        var allTools = agents.Names.SelectMany(ToolsOf).ToList();

        Assert.NotEmpty(allTools);
        Assert.All(allTools, tool => Assert.DoesNotMatch(WriteVerb(), tool));
    }

    [Fact]
    public void The_fake_model_uses_the_real_tool_names()
    {
        Assert.Equal(OrderStatusTool.Name, FakeChatClient.OrderStatusToolName);
        Assert.Equal(BillingTools.InvoiceToolName, FakeChatClient.InvoiceToolName);
        Assert.Equal(BillingTools.PaymentHistoryToolName, FakeChatClient.PaymentHistoryToolName);
        Assert.Equal(KnowledgeSearchTool.Name, FakeChatClient.KnowledgeSearchToolName);
    }

    private static IReadOnlyList<string> ToolsOf(string agent)
    {
        using var factory = SupportApiFactory.Create();
        var options = factory.Services.GetRequiredService<SupportAgents>().Get(agent).GetService<ChatClientAgentOptions>();
        Assert.NotNull(options);
        return [.. (options.ChatOptions?.Tools ?? []).OfType<AIFunctionDeclaration>().Select(tool => tool.Name).Order(StringComparer.Ordinal)];
    }

    [GeneratedRegex("cancel|refund|create|update|delete|submit|set_|issue|charge|pay_", RegexOptions.IgnoreCase)]
    private static partial Regex WriteVerb();
}
