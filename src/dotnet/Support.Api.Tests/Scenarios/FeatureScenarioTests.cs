using System.Text.Json;

namespace Support.Api.Tests.Scenarios;

/// <summary>
/// Runs the shared feature scenarios (fixtures/scenarios/feature-scenarios.json) through the version-1 API in fake
/// mode. The same file will drive the Python backend, so both implementations are held to one description of
/// behaviour. Only scenarios this build supports are listed; write and approval scenarios arrive with L10.
/// </summary>
public sealed class FeatureScenarioTests
{
    public static TheoryData<string> Supported() =>
    [
        "S01-order-status", "S02-missing-order-id", "S03-unknown-order", "S04-other-customer-order",
        "S05-invoice", "S06-payment-history", "S07-knowledge-base", "S08-unresolved-technical", "S09-unroutable",
    ];

    [Theory]
    [MemberData(nameof(Supported))]
    public async Task Scenario_behaves_as_described(string scenarioId)
    {
        var scenario = Load(scenarioId);
        var step = Assert.Single(scenario.GetProperty("steps").EnumerateArray());
        var expect = step.GetProperty("expect");

        await using var factory = SupportApiFactory.Create(principal: scenario.GetProperty("principal").GetString());
        var api = new ApiClient(factory.CreateClient());
        var turn = await api.AskAsync(await api.CreateConversationAsync(), step.GetProperty("message").GetString()!);

        var route = turn.GetProperty("route");
        if (expect.TryGetProperty("route_status", out var status))
        {
            Assert.Equal(status.GetString(), route.GetProperty("status").GetString());
        }

        if (expect.TryGetProperty("route_target", out var target))
        {
            Assert.Equal(target.GetString(), route.GetProperty("target").GetString());
        }

        Assert.Equal(expect.GetProperty("outcome").GetString(), turn.GetProperty("outcome").GetString());
        Assert.Equal(expect.GetProperty("reply_kind").GetString(), turn.ReplyKind());
        foreach (var mention in Strings(expect, "mentions"))
        {
            Assert.Contains(mention, turn.ReplyText(), StringComparison.OrdinalIgnoreCase);
        }

        foreach (var forbidden in Strings(expect, "must_not_mention"))
        {
            Assert.DoesNotContain(forbidden, turn.ReplyText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static JsonElement Load(string scenarioId)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractSchemas.RepositoryRoot(), "fixtures", "scenarios", "feature-scenarios.json")));
        return document.RootElement.GetProperty("scenarios").EnumerateArray()
            .Single(scenario => scenario.GetProperty("scenario_id").GetString() == scenarioId)
            .Clone();
    }

    private static IEnumerable<string> Strings(JsonElement expect, string name) =>
        expect.TryGetProperty(name, out var values) ? values.EnumerateArray().Select(value => value.GetString()!) : [];
}
