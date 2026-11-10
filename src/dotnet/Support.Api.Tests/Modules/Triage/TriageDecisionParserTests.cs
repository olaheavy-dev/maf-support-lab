using System.Text.Json;
using Support.Api.Modules.Triage.Application;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Tests.Modules.Triage;

/// <summary>
/// The app validates triage output in C#, and the contract states the rules in JSON Schema. These tests make the two
/// agree: on the shared contract examples and on edge cases, the parser accepts exactly what the schema accepts.
/// </summary>
public sealed class TriageDecisionParserTests
{
    private const string Valid = """
        {"target":"orders","requested_action":"order_status","order_id":"ORD-1002","summary":"Where is my order.","customer_sentiment":"neutral","confidence":0.92}
        """;

    private static readonly string ExamplesDirectory = Path.Combine(ContractSchemas.RepositoryRoot(), "contracts", "examples");

    /// <summary>Every triage example in contracts/examples, as "valid/…" or "invalid/…".</summary>
    public static TheoryData<string> ContractExamples() =>
    [
        .. Directory.GetFiles(ExamplesDirectory, "triage-*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(ExamplesDirectory, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal),
    ];

    [Theory]
    [MemberData(nameof(ContractExamples))]
    public void Shared_contract_examples_get_the_schemas_verdict(string example)
    {
        var json = File.ReadAllText(Path.Combine(ExamplesDirectory, example));

        Assert.Equal(example.StartsWith("valid", StringComparison.Ordinal), TriageDecisionParser.Parse(json).IsSuccess);
    }

    [Theory]
    [InlineData(Valid)]
    [InlineData("""{"target":"billing","requested_action":"invoice","order_id":null,"summary":"Invoice please.","customer_sentiment":"positive","confidence":0}""")]
    [InlineData("""{"target":"orders","requested_action":"order_status","order_id":"ORD-1002","summary":"x","customer_sentiment":"neutral","confidence":1}""")]
    [InlineData("""{"target":"orders","requested_action":"order_status","order_id":"ORD-1002","summary":"x","customer_sentiment":"neutral","confidence":1.01}""")]
    [InlineData("""{"target":"orders","requested_action":"order_status","order_id":"ORD-1002","summary":"x","customer_sentiment":"neutral","confidence":-0.1}""")]
    [InlineData("""{"target":"orders","requested_action":"order_status","order_id":"ord-1002","summary":"x","customer_sentiment":"neutral","confidence":0.5}""")]
    [InlineData("""{"target":"orders","requested_action":"order_status","order_id":"ORD-1002","summary":"","customer_sentiment":"neutral","confidence":0.5}""")]
    [InlineData("""{"target":"orders","requested_action":"order_status","order_id":"ORD-1002","summary":"x","customer_sentiment":"angry","confidence":0.5}""")]
    [InlineData("""{"target":"shipping","requested_action":"order_status","order_id":null,"summary":"x","customer_sentiment":"neutral","confidence":0.5}""")]
    [InlineData("""{"target":"orders","requested_action":"order_status","summary":"x","customer_sentiment":"neutral","confidence":0.5}""")]
    [InlineData("""{"target":"orders","requested_action":"order_status","order_id":null,"summary":"x","customer_sentiment":"neutral","confidence":0.5,"customer_id":"CUST-1002"}""")]
    [InlineData("""{"target":"orders","requested_action":"order_status","order_id":null,"summary":"x","customer_sentiment":"neutral","confidence":"0.5"}""")]
    [InlineData("""{"target":"orders","requested_action":"order_status","order_id":123,"summary":"x","customer_sentiment":"neutral","confidence":0.5}""")]
    [InlineData("""["orders"]""")]
    public void The_parser_accepts_exactly_what_the_contract_schema_accepts(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(ContractSchemas.IsValid("triage-decision", document.RootElement), TriageDecisionParser.Parse(json).IsSuccess);
    }

    [Fact]
    public void A_summary_over_300_characters_is_rejected_like_the_schema_does()
    {
        var json = Valid.Replace("Where is my order.", new string('a', 301), StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);

        Assert.False(ContractSchemas.IsValid("triage-decision", document.RootElement));
        Assert.False(TriageDecisionParser.Parse(json).IsSuccess);
    }

    [Theory]
    [InlineData("Sure! This looks like an order question.")]
    [InlineData("")]
    [InlineData("{\"target\":")]
    public void Text_that_is_not_JSON_is_invalid_model_output(string modelText)
    {
        var result = TriageDecisionParser.Parse(modelText);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_model_output", result.Error.Code);
    }

    [Fact]
    public void A_code_fence_around_valid_JSON_is_only_formatting()
    {
        var result = TriageDecisionParser.Parse($"```json\n{Valid}\n```");

        Assert.True(result.IsSuccess);
        Assert.Equal(RouteTarget.Orders, result.Value.Target);
        Assert.Equal("ORD-1002", result.Value.OrderId);
    }

    [Theory]
    [InlineData(RouteTarget.Orders, RequestedAction.OrderStatus, true)]
    [InlineData(RouteTarget.Billing, RequestedAction.Invoice, true)]
    [InlineData(RouteTarget.Technical, RequestedAction.Troubleshooting, true)]
    [InlineData(RouteTarget.Orders, RequestedAction.Invoice, false)]
    [InlineData(RouteTarget.Billing, RequestedAction.OrderStatus, false)]
    [InlineData(RouteTarget.Orders, RequestedAction.Other, false)]
    public void A_target_is_trusted_only_when_the_action_belongs_to_it(RouteTarget target, RequestedAction action, bool routed)
    {
        var route = RoutingRules.Route(new TriageDecision(target, action, null, "x", CustomerSentiment.Neutral, 0.99));

        Assert.Equal(routed, route.IsRouted);
        Assert.Equal(routed ? null : UnroutedReason.AmbiguousRequest, route.UnroutedReason);
    }
}
