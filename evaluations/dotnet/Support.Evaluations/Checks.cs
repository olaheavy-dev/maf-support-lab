using System.Text.Json.Nodes;

namespace Support.Evaluations;

/// <summary>One expectation compared with what was observed.</summary>
internal sealed record Check(string Name, bool Passed, JsonNode? Expected, JsonNode? Actual)
{
    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["passed"] = Passed,
        ["expected"] = Expected?.DeepClone(),
        ["actual"] = Actual?.DeepClone(),
    };
}

/// <summary>
/// Turns a step's expectation into checks. Each field is one named check, so a result says exactly which fact differed.
/// Contract facts (status, outcome, route, reply kind, operation state, tools) decide the case; "mentions" is a content
/// check on the reply text, and "must_not_mention" scans the whole result for leaked data.
/// </summary>
internal static class Checks
{
    public static IReadOnlyList<Check> Compare(Expectation expect, int httpStatus, Observed observed, Observed? previous)
    {
        List<Check> checks = [];

        void Equal<T>(string name, T? expected, T? actual)
        {
            if (expected is not null)
            {
                checks.Add(new Check(name, Equals(expected, actual), JsonValue.Create(expected), actual is null ? null : JsonValue.Create(actual)));
            }
        }

        Equal("http_status", expect.HttpStatus, httpStatus);
        Equal("problem_code", expect.ProblemCode, observed.ProblemCode);
        Equal("outcome", expect.Outcome, observed.Outcome);
        Equal("error_code", expect.ErrorCode, observed.ErrorCode);
        Equal("route_status", expect.RouteStatus, observed.RouteStatus);
        Equal("route_target", expect.RouteTarget, observed.RouteTarget);
        Equal("unrouted_reason", expect.UnroutedReason, observed.UnroutedReason);
        Equal("reply_kind", expect.ReplyKind, observed.ReplyKind);
        Equal("operation_kind", expect.OperationKind, observed.OperationKind);
        Equal("operation_status", expect.OperationStatus, observed.OperationStatus);

        if (expect.Tools is { Count: > 0 } tools)
        {
            var missing = tools.Where(tool => !observed.Tools.Contains(tool, StringComparer.Ordinal)).ToList();
            checks.Add(new Check("tools", missing.Count == 0, Strings(tools), Strings(observed.Tools)));
        }

        if (expect.ToolsAbsent is { Count: > 0 } absent)
        {
            var called = absent.Where(tool => observed.Tools.Contains(tool, StringComparer.Ordinal)).ToList();
            checks.Add(new Check("tools_absent", called.Count == 0, Strings(absent), Strings(called)));
        }

        if (expect.Mentions is { Count: > 0 } mentions)
        {
            var missing = mentions.Where(word => !observed.Text.Contains(word, StringComparison.OrdinalIgnoreCase)).ToList();
            checks.Add(new Check("mentions", missing.Count == 0, Strings(mentions), Strings(missing)));
        }

        if (expect.MustNotMention is { Count: > 0 } forbidden)
        {
            var leaked = forbidden.Where(word => observed.Raw.Contains(word, StringComparison.OrdinalIgnoreCase)).ToList();
            checks.Add(new Check("must_not_mention", leaked.Count == 0, Strings(forbidden), Strings(leaked)));
        }

        if (expect.SameTurnAsPrevious is true)
        {
            checks.Add(new Check("same_turn_as_previous", previous?.TurnId is not null && previous.TurnId == observed.TurnId, JsonValue.Create(previous?.TurnId), JsonValue.Create(observed.TurnId)));
        }

        if (expect.SameOperationAsPrevious is true)
        {
            checks.Add(new Check("same_operation_as_previous", previous?.OperationId is not null && previous.OperationId == observed.OperationId, JsonValue.Create(previous?.OperationId), JsonValue.Create(observed.OperationId)));
        }

        return checks;
    }

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(value => (JsonNode?)value)]);
}
