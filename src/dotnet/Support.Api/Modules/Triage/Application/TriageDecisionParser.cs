using System.Text.Json;
using System.Text.RegularExpressions;
using Support.Api.Common.Domain;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Triage.Application;

/// <summary>
/// Validates triage model output against the full contract schema (contracts/schemas/triage-decision.schema.json):
/// exactly the six fields, allowed values only, ORD-1234 or null, a 1-300 character summary, confidence in [0, 1].
/// Anything else is invalid model output. The schema is never loosened to fit a model; a provider that only supports
/// part of JSON Schema still has its output checked here. Error descriptions name the rule, never the model's text.
/// </summary>
public static partial class TriageDecisionParser
{
    private static readonly string[] Fields = ["target", "requested_action", "order_id", "summary", "customer_sentiment", "confidence"];

    public static Result<TriageDecision> Parse(string? modelText)
    {
        var json = StripCodeFence(modelText);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Invalid("The triage output was empty.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return Validate(document.RootElement);
        }
        catch (JsonException)
        {
            return Invalid("The triage output was not valid JSON.");
        }
    }

    private static Result<TriageDecision> Validate(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Invalid("The triage output must be a JSON object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!Fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
            {
                return Invalid("The triage output has an unknown or repeated field.");
            }
        }

        if (seen.Count != Fields.Length)
        {
            return Invalid("The triage output is missing a required field.");
        }

        if (!TryEnum(root, "target", TargetNames, out RouteTarget target) ||
            !TryEnum(root, "requested_action", ActionNames, out RequestedAction action) ||
            !TryEnum(root, "customer_sentiment", SentimentNames, out CustomerSentiment sentiment))
        {
            return Invalid("The triage output has a value outside its allowed set.");
        }

        var orderIdElement = root.GetProperty("order_id");
        string? orderId = null;
        if (orderIdElement.ValueKind == JsonValueKind.String && OrderIdPattern().IsMatch(orderIdElement.GetString()!))
        {
            orderId = orderIdElement.GetString();
        }
        else if (orderIdElement.ValueKind != JsonValueKind.Null)
        {
            return Invalid("order_id must be null or look like ORD-1234.");
        }

        var summaryElement = root.GetProperty("summary");
        var summary = summaryElement.ValueKind == JsonValueKind.String ? summaryElement.GetString()! : null;
        var summaryLength = summary?.EnumerateRunes().Count() ?? 0;
        if (summary is null || summaryLength is < 1 or > 300)
        {
            return Invalid("summary must be a string of 1 to 300 characters.");
        }

        var confidenceElement = root.GetProperty("confidence");
        if (confidenceElement.ValueKind != JsonValueKind.Number || !confidenceElement.TryGetDouble(out var confidence) || confidence is < 0 or > 1)
        {
            return Invalid("confidence must be a number from 0 to 1.");
        }

        return Result.Success(new TriageDecision(target, action, orderId, summary, sentiment, confidence));
    }

    private static readonly Dictionary<string, RouteTarget> TargetNames = new(StringComparer.Ordinal)
    {
        ["orders"] = RouteTarget.Orders,
        ["billing"] = RouteTarget.Billing,
        ["technical"] = RouteTarget.Technical,
    };

    private static readonly Dictionary<string, RequestedAction> ActionNames =
        Enum.GetValues<RequestedAction>().ToDictionary(Names.Of, action => action, StringComparer.Ordinal);

    private static readonly Dictionary<string, CustomerSentiment> SentimentNames = new(StringComparer.Ordinal)
    {
        ["frustrated"] = CustomerSentiment.Frustrated,
        ["neutral"] = CustomerSentiment.Neutral,
        ["positive"] = CustomerSentiment.Positive,
    };

    private static bool TryEnum<T>(JsonElement root, string name, Dictionary<string, T> allowed, out T value)
    {
        value = default!;
        var element = root.GetProperty(name);
        return element.ValueKind == JsonValueKind.String && allowed.TryGetValue(element.GetString()!, out value!);
    }

    /// <summary>Models sometimes wrap JSON in a ```json fence. Removing a fence that encloses the whole text changes formatting, not content.</summary>
    private static string? StripCodeFence(string? text)
    {
        var trimmed = text?.Trim();
        if (trimmed is null || !trimmed.StartsWith("```", StringComparison.Ordinal) || !trimmed.EndsWith("```", StringComparison.Ordinal) || trimmed.Length < 6)
        {
            return trimmed;
        }

        var firstNewLine = trimmed.IndexOf('\n', StringComparison.Ordinal);
        return firstNewLine < 0 ? trimmed : trimmed[(firstNewLine + 1)..^3].Trim();
    }

    private static Result<TriageDecision> Invalid(string rule) =>
        Result.Failure<TriageDecision>(Error.Validation("invalid_model_output", rule));

    [GeneratedRegex("^ORD-[0-9]{4}$")]
    private static partial Regex OrderIdPattern();
}
