using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Support.Api.Tests.Modules.Agents;

/// <summary>
/// Opt-in live checks against Claude. Never run in CI or a normal `dotnet test`: they are Explicit and need
/// ANTHROPIC_API_KEY. Bounded: one turn per domain, each about three model calls (triage, then the specialist and
/// its tool round trip).
/// Run: dotnet test --solution maf-support-lab.slnx -- --filter-trait "Category=Live" --explicit only
/// </summary>
public sealed class LiveSmokeTests
{
    [Fact(Explicit = true)]
    [Trait("Category", "Live")]
    public async Task Claude_answers_a_known_order_through_the_tool()
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(key), "Set ANTHROPIC_API_KEY to run the live smoke check.");

        await using var factory = SupportApiFactory.Create(new Dictionary<string, string?>
        {
            ["ModelProvider:Mode"] = "Live",
            ["ModelProvider:Anthropic:Model"] = "claude-haiku-5-5",
            ["ModelProvider:Anthropic:ApiKey"] = key,
        });
        var api = new ApiClient(factory.CreateClient());
        var provider = factory.Services.GetRequiredService<IChatClient>().GetService<ChatClientMetadata>();
        Assert.NotEqual("fake", provider?.ProviderName);

        var turn = await api.AskAsync(await api.CreateConversationAsync(), "Where is my order ORD-1002?");

        TestContext.Current.SendDiagnosticMessage($"Live provider={provider?.ProviderName} model={provider?.DefaultModelId} reply={turn.ReplyText()}");
        // A routed route proves Claude's triage output passed full contract validation (L07 structured triage).
        Assert.Equal("routed", turn.GetProperty("route").GetProperty("status").GetString());
        Assert.Equal("orders", turn.GetProperty("route").GetProperty("target").GetString());
        Assert.Equal("answer", turn.ReplyKind());
        Assert.Contains("ship", turn.ReplyText(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory(Explicit = true)]
    [Trait("Category", "Live")]
    [InlineData("Can you send me the invoice for ORD-1001?", "billing", "INV-5001")]
    [InlineData("My wireless headphones keep crackling.", "technical", "KB-102")]
    public async Task Claude_answers_billing_and_technical_from_their_tools(string message, string target, string identifier)
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(key), "Set ANTHROPIC_API_KEY to run the live smoke check.");

        await using var factory = SupportApiFactory.Create(new Dictionary<string, string?>
        {
            ["ModelProvider:Mode"] = "Live",
            ["ModelProvider:Anthropic:Model"] = "claude-haiku-5-5",
            ["ModelProvider:Anthropic:ApiKey"] = key,
        });
        var api = new ApiClient(factory.CreateClient());

        var turn = await api.AskAsync(await api.CreateConversationAsync(), message);

        // S05 / S07 with real Claude: routed by validated triage, answered from a tool, citing the record it used.
        Assert.Equal("routed", turn.GetProperty("route").GetProperty("status").GetString());
        Assert.Equal(target, turn.GetProperty("route").GetProperty("target").GetString());
        Assert.Equal("answer", turn.ReplyKind());
        Assert.Contains(identifier, turn.ReplyText(), StringComparison.Ordinal);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Live")]
    public async Task Claude_answers_an_order_follow_up_without_the_id()
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(key), "Set ANTHROPIC_API_KEY to run the live smoke check.");

        await using var factory = SupportApiFactory.Create(new Dictionary<string, string?>
        {
            ["ModelProvider:Mode"] = "Live",
            ["ModelProvider:Anthropic:Model"] = "claude-haiku-5-5",
            ["ModelProvider:Anthropic:ApiKey"] = key,
        });
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        await api.AskAsync(conversation, "Where is my order ORD-1002?");
        var followUp = await api.AskAsync(conversation, "When was it shipped?");

        // L09 with real Claude: routed to orders from the follow-up alone, answered from the tool for the verified order.
        Assert.Equal("orders", followUp.GetProperty("route").GetProperty("target").GetString());
        Assert.Equal("answer", followUp.ReplyKind());
        Assert.Contains("October 30", followUp.ReplyText(), StringComparison.OrdinalIgnoreCase);
    }
}
