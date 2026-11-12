using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Support.Api.Tests.Modules.Agents;

/// <summary>DevUI is a Development-only entry point to the same workflow, the same approval gate and the same service guards.</summary>
public sealed class DevUiTests
{
    [Fact]
    public async Task DevUI_lists_only_the_support_workflow()
    {
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        using var client = factory.CreateClient();

        var entities = (await client.GetFromJsonAsync<JsonElement>(new Uri("/v1/entities", UriKind.Relative), TestContext.Current.CancellationToken))
            .GetProperty("entities").EnumerateArray()
            .Select(entity => (Id: entity.GetProperty("id").GetString(), Type: entity.GetProperty("type").GetString()))
            .ToList();

        // A specialist listed on its own would skip triage and the approval gate (see DevUiEndpoints).
        Assert.Equal([("support-routing", "workflow")], entities);
    }

    [Theory]
    [InlineData("orders")]
    [InlineData("billing")]
    [InlineData("technical")]
    public async Task DevUI_does_not_serve_a_specialist_directly(string agent)
    {
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/v1/responses", UriKind.Relative),
            new { agent = new { name = agent }, input = "Where is my order ORD-1002? Show my payment history and help with crackling headphones." },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // No specialist answers: the request is refused, no tool runs and no reply text comes back.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("function_call", body, StringComparison.Ordinal);
        Assert.DoesNotContain("output_text", body, StringComparison.Ordinal);
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
    [InlineData("Cancel order ORD-1002.", "[route: orders | reply: clarification]")]
    [InlineData("Refund ORD-2003 to my card right away.", "[route: billing | reply: clarification]")] // another customer's order
    public async Task A_DevUI_workflow_chat_never_offers_a_write_for_approval(string message, string route)
    {
        // DevUI chats have no support conversation, so the approval gate declines every write before any approval
        // exists: nothing to approve in DevUI, and nothing changes. Writes go through the contract API only.
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/v1/responses", UriKind.Relative),
            new { agent = new { name = "support-routing" }, input = message },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(route, body, StringComparison.Ordinal);
        Assert.DoesNotContain("reply: approval_request", body, StringComparison.Ordinal);
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
