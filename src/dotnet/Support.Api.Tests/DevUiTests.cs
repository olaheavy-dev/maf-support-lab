using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Support.Api.Tests;

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
