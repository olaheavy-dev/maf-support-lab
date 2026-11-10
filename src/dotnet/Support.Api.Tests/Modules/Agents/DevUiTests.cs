using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Support.Api.Tests.Modules.Agents;

/// <summary>DevUI is a Development-only entry point to the same agent and the same service guards.</summary>
public sealed class DevUiTests
{
    [Fact]
    public async Task DevUI_lists_the_orders_agent_and_its_tool()
    {
        await using var factory = SupportApiFactory.Create();
        using var client = factory.CreateClient();

        var entities = await client.GetFromJsonAsync<JsonElement>(new Uri("/v1/entities", UriKind.Relative), TestContext.Current.CancellationToken);

        var orders = entities.GetProperty("entities").EnumerateArray().Single(entity => entity.GetProperty("id").GetString() == "orders");
        Assert.Equal("agent", orders.GetProperty("type").GetString());
        Assert.Contains("get_order_status", orders.GetProperty("tools").EnumerateArray().Select(tool => tool.GetString()));
    }

    [Fact]
    public async Task DevUI_requests_go_through_the_same_ownership_guard()
    {
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/v1/responses", UriKind.Relative),
            new { agent = new { name = "orders" }, input = "What's the status of ORD-2001?" },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("couldn't find order ORD-2001", body, StringComparison.Ordinal);
        Assert.DoesNotContain("delivered", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DevUI_lists_the_support_workflow_and_its_specialists()
    {
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        using var client = factory.CreateClient();

        var entities = (await client.GetFromJsonAsync<JsonElement>(new Uri("/v1/entities", UriKind.Relative), TestContext.Current.CancellationToken))
            .GetProperty("entities").EnumerateArray().ToDictionary(entity => entity.GetProperty("id").GetString()!, entity => entity.GetProperty("type").GetString());

        Assert.Equal("workflow", entities["support-routing"]);
        Assert.Equal("agent", entities["orders"]);
        Assert.Equal("agent", entities["billing"]);
        Assert.Equal("agent", entities["technical"]);
    }

    [Theory]
    [InlineData("Can you send me the invoice for ORD-1001?", "[route: billing | reply: answer]", "INV-5001")]
    [InlineData("What's the status of ORD-2001?", "[route: orders | reply: not_found]", "ORD-2001")]
    [InlineData("asdf qwer zxcv", "[route: unrouted (ambiguous_request) | reply: clarification]", "order, billing, or a technical problem")]
    public async Task A_DevUI_chat_with_the_workflow_goes_through_triage_and_the_same_guards(string message, string route, string expected)
    {
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/v1/responses", UriKind.Relative),
            new { agent = new { name = "support-routing" }, input = message },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(route, body, StringComparison.Ordinal);
        Assert.Contains(expected, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/devui/")]
    [InlineData("/v1/entities")]
    [InlineData("/v1/responses")]
    public async Task Development_tools_are_not_mapped_outside_Development(string path)
    {
        await using var factory = SupportApiFactory.Create(environment: "Production", principal: "CUST-1001");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
