using System.Text.Json;
using System.Text.Json.Nodes;
using Support.Api.Common.Domain;

namespace Support.Api.Common.Infrastructure;

/// <summary>Loads <see cref="SupportPolicy"/> from the policies fixture copied next to the binaries.</summary>
internal static class FixturePolicy
{
    public static SupportPolicy Load(string? directory = null)
    {
        directory ??= Path.Combine(AppContext.BaseDirectory, "fixtures");
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "policies.json")))
            ?? throw new InvalidOperationException("fixtures/policies.json is empty.");

        static IReadOnlyList<string> Strings(JsonNode? node) => [.. node!.AsArray().Select(item => item!.GetValue<string>())];

        return new SupportPolicy(
            DateTimeOffset.Parse(root["reference_time"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
            Strings(root["cancellation"]!["allowed_statuses"]),
            root["refund"]!["required_status"]!.GetValue<string>(),
            root["refund"]!["window_days"]!.GetValue<int>(),
            Strings(root["refund"]!["eligible_refund_statuses"]),
            Strings(root["support_ticket"]!["priorities"]),
            root["approval"]!["expires_after_minutes"]!.GetValue<int>());
    }
}
