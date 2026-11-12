using System.Text.Json;
using Support.Api.Modules.Agents.Infrastructure.ModelProvider;

namespace Support.Api.Tests.Modules.Triage;

/// <summary>
/// Routing through the API: every message is triaged, and code decides the route. Responses are validated
/// against the contract by ApiClient. The counting model counts specialist calls only (triage is separate).
/// </summary>
public sealed class RoutingTests
{
    [Fact]
    public async Task An_order_question_goes_to_the_real_orders_specialist()
    {
        var model = new CountingScript();
        var turn = await AskAsync("Where is my order ORD-1002?", model);

        AssertRoute(turn, "routed", "orders");
        Assert.Equal("answer", turn.ReplyKind());
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task An_order_question_without_an_order_id_asks_for_it_without_a_model_call()
    {
        var model = new CountingScript();
        var turn = await AskAsync("Where is my package?", model);

        AssertRoute(turn, "routed", "orders");
        Assert.Equal("clarification", turn.ReplyKind());
        Assert.Contains("ORD-1234", turn.ReplyText(), StringComparison.Ordinal);
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData("Where is ORD-1002? " + FakeChatClient.MalformedTriageTrigger, "invalid_model_output")]
    [InlineData("Where is ORD-1002? " + FakeChatClient.UnknownRouteTrigger, "invalid_model_output")]
    [InlineData("Hello there, nice weather today.", "ambiguous_request")]
    public async Task Invalid_or_ambiguous_triage_is_never_guessed(string message, string reason)
    {
        var model = new CountingScript();
        var turn = await AskAsync(message, model);

        AssertRoute(turn, "unrouted", null, reason);
        Assert.Equal("clarification", turn.ReplyKind());
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_target_that_disagrees_with_the_action_is_ambiguous_not_trusted()
    {
        var model = new CountingScript();
        var turn = await AskAsync("Where is my order ORD-1002?", model, Triage("billing", "order_status", confidence: 0.99));

        AssertRoute(turn, "unrouted", null, "ambiguous_request");
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(1.0)]
    public async Task Model_confidence_never_changes_the_route(double confidence)
    {
        var turn = await AskAsync("Where is my order ORD-1002?", new CountingScript(), Triage("orders", "order_status", confidence));

        AssertRoute(turn, "routed", "orders");
        Assert.Equal("answer", turn.ReplyKind());
    }

    [Fact]
    public async Task Triage_cannot_widen_access_by_naming_another_customers_order()
    {
        // The triage order_id is only a claim. Ownership is still decided by the orders handler for the server's principal.
        var turn = await AskAsync("Show me ORD-2001", new CountingScript());

        AssertRoute(turn, "routed", "orders");
        Assert.Equal("not_found", turn.ReplyKind());
    }

    private static async Task<JsonElement> AskAsync(string message, CountingScript model, Func<string, string>? triage = null)
    {
        await using var factory = SupportApiFactory.Create(chatClient: model.Client(triage));
        var api = new ApiClient(factory.CreateClient());
        return await api.AskAsync(await api.CreateConversationAsync(), message);
    }

    private static Func<string, string> Triage(string target, string action, double confidence) => _ =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["target"] = target,
            ["requested_action"] = action,
            ["order_id"] = "ORD-1002",
            ["summary"] = "Customer asks about an order.",
            ["customer_sentiment"] = "neutral",
            ["confidence"] = confidence,
        });

    private static void AssertRoute(JsonElement turn, string status, string? target, string? reason = null)
    {
        var route = turn.GetProperty("route");
        Assert.Equal(status, route.GetProperty("status").GetString());
        Assert.Equal(target, route.GetProperty("target").GetString());
        Assert.Equal(reason, route.TryGetProperty("unrouted_reason", out var r) ? r.GetString() : null);
    }
}
