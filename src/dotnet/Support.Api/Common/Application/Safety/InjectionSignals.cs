using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Support.Api.Common.Application.Observability;

namespace Support.Api.Common.Application.Safety;

/// <summary>
/// A <b>diagnostic</b> prompt-injection heuristic (L14), in the spirit of the course's phrase matcher. It looks for
/// well-known attack phrasings in customer text and tool results and records what it found on the current span and as
/// a metric, so attacks can be studied. It never blocks, rewrites, or decides anything: it has false positives
/// ("please ignore my previous message") and is bypassed by any paraphrase. What keeps customers safe is enforced in
/// code whatever the text says: identity, ownership, eligibility, and bound approval.
/// Only signal names are recorded, never the text itself.
/// </summary>
public sealed partial class InjectionSignals
{
    private static readonly (string Name, Regex Pattern)[] Signals =
    [
        ("ignore_instructions", IgnoreInstructions()),
        ("role_override", RoleOverride()),
        ("prompt_extraction", PromptExtraction()),
        ("fake_system_message", FakeSystemMessage()),
        ("skip_approval", SkipApproval()),
    ];

    private readonly Counter<long> counter;

    public InjectionSignals(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        counter = meterFactory.Create(SupportTelemetry.Name)
            .CreateCounter<long>("support.injection.signals", unit: "{signal}", description: "Prompt-injection phrasings seen (diagnostic only; never used to decide anything).");
    }

    /// <summary>The names of the signals found in <paramref name="text"/> (empty when none).</summary>
    public static IReadOnlyList<string> Find(string? text) =>
        string.IsNullOrEmpty(text) ? [] : [.. Signals.Where(signal => signal.Pattern.IsMatch(text)).Select(signal => signal.Name)];

    /// <summary>Finds signals and records them on the current span and the metric. Returns the names found.</summary>
    public IReadOnlyList<string> Inspect(string source, string? text)
    {
        var found = Find(text);
        if (found.Count > 0)
        {
            Activity.Current?.SetTag($"support.injection.{source}", string.Join(',', found));
            foreach (var name in found)
            {
                counter.Add(1, new KeyValuePair<string, object?>("support.injection.source", source), new KeyValuePair<string, object?>("support.injection.signal", name));
            }
        }

        return found;
    }

    [GeneratedRegex(@"\b(ignore|disregard|forget)\b.{0,30}\b(previous|prior|above|earlier|all|your|the)\b.{0,20}\b(instructions?|rules|guidelines|prompts?|messages?)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IgnoreInstructions();

    [GeneratedRegex(@"\b(you are now|act as|pretend to be|you're now)\b.{0,30}\b(admin|administrator|root|developer|system|unrestricted|dan)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RoleOverride();

    [GeneratedRegex(@"\b(system prompt|your instructions|developer message|reveal your (rules|prompt))\b", RegexOptions.IgnoreCase)]
    private static partial Regex PromptExtraction();

    [GeneratedRegex(@"(^|\n)\s*(\[?system\]?|###\s*system|<\s*system\s*>)\s*[:>\]]", RegexOptions.IgnoreCase)]
    private static partial Regex FakeSystemMessage();

    [GeneratedRegex(@"\b(without|skip|no need for|don't ask for|bypass)\b.{0,20}\b(approval|confirmation|permission|asking)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SkipApproval();
}
