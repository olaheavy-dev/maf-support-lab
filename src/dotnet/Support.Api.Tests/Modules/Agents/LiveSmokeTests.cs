using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Support.Api.Tests.Modules.Agents;

/// <summary>
/// Opt-in live check against Claude. Never runs in CI or a normal `dotnet test`: it is Explicit and
/// needs ANTHROPIC_API_KEY. Bounded to one turn (two model calls with the tool round trip).
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
        Assert.Equal("answer", turn.ReplyKind());
        Assert.Contains("ship", turn.ReplyText(), StringComparison.OrdinalIgnoreCase);
    }
}
