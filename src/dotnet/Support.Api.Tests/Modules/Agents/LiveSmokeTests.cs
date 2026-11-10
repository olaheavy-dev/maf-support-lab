using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Support.Api.Tests.Modules.Agents;

/// <summary>
/// Opt-in live checks against Claude. Never run in CI or a normal `dotnet test`: they are Explicit and need
/// ANTHROPIC_API_KEY. Bounded: the order turn is three model calls (triage, then the orders agent and its tool
/// round trip); the billing turn is one (triage only, the billing specialist is a placeholder until L08).
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

    [Fact(Explicit = true)]
    [Trait("Category", "Live")]
    public async Task Claude_triage_routes_an_invoice_question_to_billing()
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

        var turn = await api.AskAsync(await api.CreateConversationAsync(), "Could you email me the invoice for my last purchase?");

        Assert.Equal("routed", turn.GetProperty("route").GetProperty("status").GetString());
        Assert.Equal("billing", turn.GetProperty("route").GetProperty("target").GetString());
        Assert.Equal("escalation_offer", turn.ReplyKind());
    }
}
