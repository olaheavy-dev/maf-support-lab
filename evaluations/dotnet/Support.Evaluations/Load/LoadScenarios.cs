using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Support.Evaluations.Load;

/// <summary>A named check inside a scenario: what was required, and what was seen.</summary>
internal sealed record LoadCheck(string Name, bool Passed, string Detail)
{
    public JsonObject ToJson() => new() { ["name"] = Name, ["passed"] = Passed, ["detail"] = Detail };
}

/// <summary>One scenario's measurements and checks.</summary>
internal sealed record ScenarioResult(string Name, string Purpose, int Concurrency, IReadOnlyList<Timed> Requests, double WallMilliseconds, IReadOnlyList<LoadCheck> Checks)
{
    public bool Passed => Checks.All(c => c.Passed);

    public JsonObject ToJson()
    {
        var latencies = Requests.Select(r => r.Milliseconds).Order().ToList();
        double Percentile(double p) => latencies.Count == 0 ? 0 : Math.Round(latencies[Math.Clamp((int)Math.Ceiling(p * latencies.Count) - 1, 0, latencies.Count - 1)], 1);
        JsonObject Count(IEnumerable<string> keys)
        {
            var json = new JsonObject();
            foreach (var group in keys.GroupBy(k => k).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                json[group.Key] = group.Count();
            }

            return json;
        }

        return new JsonObject
        {
            ["name"] = Name,
            ["purpose"] = Purpose,
            ["concurrency"] = Concurrency,
            ["requests"] = Requests.Count,
            ["wall_ms"] = Math.Round(WallMilliseconds, 1),
            ["throughput_rps"] = WallMilliseconds <= 0 ? 0 : Math.Round(Requests.Count / (WallMilliseconds / 1000), 2),
            ["latency_ms"] = new JsonObject
            {
                ["p50"] = Percentile(0.50),
                ["p95"] = Percentile(0.95),
                ["p99"] = Percentile(0.99),
                ["max"] = latencies.Count == 0 ? 0 : Math.Round(latencies[^1], 1),
                ["mean"] = latencies.Count == 0 ? 0 : Math.Round(latencies.Average(), 1),
            },
            ["http_status"] = Count(Requests.Select(r => r.Status.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            ["outcomes"] = Count(Requests.Select(r => r.Text("outcome") ?? r.Text("code") ?? (r.Text("conversation_id") is not null && r.Text("turn_id") is null ? "conversation_created" : "other"))),
            ["status"] = Passed ? "passed" : "failed",
            ["checks"] = new JsonArray([.. Checks.Select(c => (JsonNode?)c.ToJson())]),
        };
    }
}

/// <summary>
/// The fault and bounded-load scenarios (L25), run black-box against one backend in fake model mode. Each answers a
/// question the deterministic suite asks one request at a time, but under concurrency: does a conversation ever see
/// another's data, does a write ever run twice, does one failure spill into other conversations, and how do latency
/// and throughput behave as concurrency grows. Writes run on a fresh store per scenario.
/// </summary>
internal static partial class LoadScenarios
{
    public static readonly IReadOnlyDictionary<string, string[]> OwnedOrders = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["CUST-1001"] = ["ORD-1001", "ORD-1002", "ORD-1003"],
        ["CUST-1002"] = ["ORD-2001", "ORD-2002", "ORD-2003"],
    };

    [GeneratedRegex("ORD-[0-9]{4}")]
    private static partial Regex OrderId();

    /// <summary>
    /// Many conversations at once, across both customers, each asking only about its own orders. Every reply must name
    /// the order it was asked about and no other; every turn must belong to the conversation that sent it.
    /// </summary>
    public static async Task<ScenarioResult> IsolationAsync(LoadClient client, IReadOnlyDictionary<string, Uri> principals, int concurrency, int totalTurns, CancellationToken cancellationToken)
    {
        // The same total work at every level, split across `concurrency` conversations, so levels compare fairly.
        var turnsPerConversation = (int)Math.Ceiling(totalTurns / (double)concurrency);
        var clock = Stopwatch.StartNew();
        var work = Enumerable.Range(0, concurrency).Select(async index =>
        {
            var customer = index % 2 == 0 ? "CUST-1001" : "CUST-1002";
            var url = principals[customer];
            var created = await client.CreateConversationAsync(url, cancellationToken).ConfigureAwait(false);
            var conversation = created.Text("conversation_id")!;
            List<(Timed Turn, string Asked, string Conversation)> turns = [];
            for (var t = 0; t < turnsPerConversation; t++)
            {
                var asked = OwnedOrders[customer][(index + t) % 3];
                turns.Add((await client.TurnAsync(url, conversation, $"Where is my order {asked}?", null, cancellationToken).ConfigureAwait(false), asked, conversation));
            }

            return (Created: created, Turns: turns);
        });
        var results = await Task.WhenAll(work).ConfigureAwait(false);
        clock.Stop();

        var turnsSeen = results.SelectMany(r => r.Turns).ToList();
        var leaks = turnsSeen.Where(t => OrderId().Matches(t.Turn.Text("reply", "text") ?? "").Select(m => m.Value).Any(id => id != t.Asked)).ToList();
        var unanswered = turnsSeen.Where(t => t.Turn.Status != 200 || t.Turn.Text("outcome") != "completed" || !(t.Turn.Text("reply", "text") ?? "").Contains(t.Asked, StringComparison.Ordinal)).ToList();
        var wrongConversation = turnsSeen.Where(t => t.Turn.Text("conversation_id") != t.Conversation).ToList();
        var turnIds = turnsSeen.Select(t => t.Turn.Text("turn_id")).ToList();
        return new ScenarioResult(
            $"isolation-c{concurrency}",
            $"{concurrency} concurrent conversations across both customers, {turnsPerConversation} turns each, each asking about its own orders only.",
            concurrency,
            [.. results.Select(r => r.Created), .. turnsSeen.Select(t => t.Turn)],
            clock.Elapsed.TotalMilliseconds,
            [
                new LoadCheck("every turn answered about the order it asked", unanswered.Count == 0, $"{turnsSeen.Count - unanswered.Count}/{turnsSeen.Count} completed and named their own order"),
                new LoadCheck("no reply names another order", leaks.Count == 0, leaks.Count == 0 ? "none" : string.Join("; ", leaks.Take(3).Select(l => $"asked {l.Asked}, reply: {l.Turn.Text("reply", "text")}"))),
                new LoadCheck("every turn belongs to the conversation that sent it", wrongConversation.Count == 0, $"{wrongConversation.Count} mismatches"),
                new LoadCheck("every turn id is unique", turnIds.Distinct().Count() == turnIds.Count, $"{turnIds.Distinct().Count()} distinct of {turnIds.Count}"),
            ]);
    }

    /// <summary>The same client_turn_id sent many times at once: one turn, every response naming it.</summary>
    public static async Task<ScenarioResult> DuplicateSubmissionAsync(LoadClient client, Uri url, int copies, CancellationToken cancellationToken)
    {
        var conversation = (await client.CreateConversationAsync(url, cancellationToken).ConfigureAwait(false)).Text("conversation_id")!;
        var id = Guid.NewGuid().ToString();
        var clock = Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(0, copies).Select(_ => client.TurnAsync(url, conversation, "Where is my order ORD-1002?", id, cancellationToken))).ConfigureAwait(false);
        clock.Stop();
        var ids = responses.Select(r => r.Text("turn_id")).Distinct().ToList();
        return new ScenarioResult(
            "duplicate-submission",
            $"{copies} copies of one turn (same client_turn_id) at once.",
            copies,
            responses,
            clock.Elapsed.TotalMilliseconds,
            [
                new LoadCheck("every copy is accepted (200)", responses.All(r => r.Status == 200), string.Join(",", responses.Select(r => r.Status).Distinct())),
                new LoadCheck("all copies name one turn", ids.Count == 1 && ids[0] is not null, $"{ids.Count} distinct turn ids"),
            ]);
    }

    /// <summary>While a (slow) turn runs, concurrent turns in the same conversation are refused; the slow one is then cancelled.</summary>
    public static async Task<ScenarioResult> TurnInProgressAsync(LoadClient client, Uri url, int attempts, CancellationToken cancellationToken)
    {
        var conversation = (await client.CreateConversationAsync(url, cancellationToken).ConfigureAwait(false)).Text("conversation_id")!;
        var slow = client.TurnAsync(url, conversation, "Where is my order ORD-1002? [fake:slow-model]", null, cancellationToken);
        string? active = null;
        for (var i = 0; i < 100 && active is null; i++)
        {
            active = (await client.GetAsync(new Uri(url, $"api/v1/conversations/{conversation}"), cancellationToken).ConfigureAwait(false)).Text("active_turn_id");
            if (active is null)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        var clock = Stopwatch.StartNew();
        var refused = await Task.WhenAll(Enumerable.Range(0, attempts).Select(_ => client.TurnAsync(url, conversation, "Where is my order ORD-1001?", null, cancellationToken))).ConfigureAwait(false);
        clock.Stop();
        var cancel = active is null ? null : await client.CancelAsync(url, conversation, active, cancellationToken).ConfigureAwait(false);
        var finished = await slow.ConfigureAwait(false);
        return new ScenarioResult(
            "turn-in-progress",
            $"{attempts} turns sent to a conversation whose (slow) turn is running, then that turn is cancelled.",
            attempts,
            refused,
            clock.Elapsed.TotalMilliseconds,
            [
                new LoadCheck("the slow turn was seen running", active is not null, active ?? "never became active"),
                new LoadCheck("every concurrent turn is refused with 409 turn_in_progress", refused.All(r => r.Status == 409 && r.Text("code") == "turn_in_progress"), string.Join(",", refused.Select(r => $"{r.Status} {r.Text("code")}").Distinct())),
                new LoadCheck("the cancel is accepted (202)", cancel?.Status == 202, $"{cancel?.Status}"),
                new LoadCheck("the slow turn ends cancelled", finished.Text("outcome") == "cancelled", finished.Text("outcome") ?? $"HTTP {finished.Status}"),
            ]);
    }

    /// <summary>One approval decided "approve" many times at once: the write runs once, every response reports it.</summary>
    public static async Task<ScenarioResult> DuplicateApprovalAsync(LoadClient client, Uri url, int copies, CancellationToken cancellationToken)
    {
        var (conversation, approval, operation, proposal) = await ProposeAsync(client, url, "Please cancel order ORD-1003.", cancellationToken).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(0, copies).Select(_ => client.DecideAsync(url, conversation, approval, operation, "approve", cancellationToken))).ConfigureAwait(false);
        clock.Stop();
        var record = await client.GetAsync(new Uri(url, $"api/v1/operations/{operation}"), cancellationToken).ConfigureAwait(false);
        var follow = await client.TurnAsync(url, conversation, "Where is my order ORD-1003?", null, cancellationToken).ConfigureAwait(false);
        var turns = responses.Select(r => r.Text("turn_id")).Distinct().ToList();
        return new ScenarioResult(
            "duplicate-approval",
            $"One cancellation approved {copies} times at once.",
            copies,
            responses,
            clock.Elapsed.TotalMilliseconds,
            [
                new LoadCheck("the write was proposed", proposal.Text("outcome") == "awaiting_approval", proposal.Text("outcome") ?? $"HTTP {proposal.Status}"),
                new LoadCheck("every decision is accepted (200, idempotent)", responses.All(r => r.Status == 200), string.Join(",", responses.Select(r => r.Status).Distinct())),
                new LoadCheck("every response is the same resumed turn", turns.Count == 1, $"{turns.Count} distinct resumed turns"),
                new LoadCheck("the operation committed once", record.Text("status") == "committed", record.Text("status") ?? $"HTTP {record.Status}"),
                new LoadCheck("the order shows the change once (cancelled)", (follow.Text("reply", "text") ?? "").Contains("cancelled", StringComparison.OrdinalIgnoreCase), follow.Text("reply", "text") ?? ""),
            ]);
    }

    /// <summary>Approve and reject sent at once for one operation: exactly one decision wins and the other side gets 409.</summary>
    public static async Task<ScenarioResult> DecisionRaceAsync(LoadClient client, Uri url, int each, CancellationToken cancellationToken)
    {
        var (conversation, approval, operation, proposal) = await ProposeAsync(client, url, "I want a refund for ORD-1001, the headphones crackle.", cancellationToken).ConfigureAwait(false);
        var decisions = Enumerable.Range(0, each * 2).Select(i => i % 2 == 0 ? "approve" : "reject").ToList();
        var clock = Stopwatch.StartNew();
        var responses = await Task.WhenAll(decisions.Select(d => client.DecideAsync(url, conversation, approval, operation, d, cancellationToken))).ConfigureAwait(false);
        clock.Stop();
        var record = await client.GetAsync(new Uri(url, $"api/v1/operations/{operation}"), cancellationToken).ConfigureAwait(false);
        var winners = decisions.Zip(responses).Where(p => p.Second.Status == 200).Select(p => p.First).Distinct().ToList();
        var losers = decisions.Zip(responses).Where(p => p.Second.Status != 200).ToList();
        var expected = winners.Count == 1 ? (winners[0] == "approve" ? "committed" : "rejected") : "?";
        return new ScenarioResult(
            "decision-race",
            $"{each} approvals and {each} rejections of one refund, all at once.",
            each * 2,
            responses,
            clock.Elapsed.TotalMilliseconds,
            [
                new LoadCheck("the write was proposed", proposal.Text("outcome") == "awaiting_approval", proposal.Text("outcome") ?? $"HTTP {proposal.Status}"),
                new LoadCheck("exactly one decision wins", winners.Count == 1, winners.Count == 1 ? winners[0] : $"{winners.Count} decisions succeeded"),
                new LoadCheck("every other-decision request gets 409 approval_already_decided", losers.All(l => l.Second.Status == 409 && l.Second.Text("code") == "approval_already_decided") && losers.All(l => winners.Count == 1 && l.First != winners[0]), string.Join(",", losers.Select(l => $"{l.First}:{l.Second.Status} {l.Second.Text("code")}").Distinct())),
                new LoadCheck("the operation matches the winning decision", record.Text("status") == expected, $"{record.Text("status")} (expected {expected})"),
            ]);
    }

    /// <summary>Model failures in some conversations while others run: failures stay in their own conversations.</summary>
    public static async Task<ScenarioResult> FaultsUnderLoadAsync(LoadClient client, Uri url, int concurrency, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var runs = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async index =>
        {
            var faulty = index % 2 == 1;
            var conversation = (await client.CreateConversationAsync(url, cancellationToken).ConfigureAwait(false)).Text("conversation_id")!;
            var turn = await client.TurnAsync(url, conversation, faulty ? "Where is ORD-1002? [fake:model-error]" : "Where is my order ORD-1002?", null, cancellationToken).ConfigureAwait(false);
            var after = await client.TurnAsync(url, conversation, "Where is my order ORD-1001?", null, cancellationToken).ConfigureAwait(false);
            return (Faulty: faulty, Turn: turn, After: after);
        })).ConfigureAwait(false);
        clock.Stop();
        var faulty = runs.Where(r => r.Faulty).ToList();
        var healthy = runs.Where(r => !r.Faulty).ToList();
        return new ScenarioResult(
            "faults-under-load",
            $"{concurrency} conversations at once; half hit a model failure.",
            concurrency,
            [.. runs.Select(r => r.Turn), .. runs.Select(r => r.After)],
            clock.Elapsed.TotalMilliseconds,
            [
                new LoadCheck("every injected failure is a failed turn (internal_error, as the support-failure dataset expects)", faulty.All(r => r.Turn.Status == 200 && r.Turn.Text("outcome") == "failed" && r.Turn.Text("error", "code") == "internal_error"), string.Join(",", faulty.Select(r => $"{r.Turn.Text("outcome")}/{r.Turn.Text("error", "code")}").Distinct())),
                new LoadCheck("every healthy conversation completes", healthy.All(r => r.Turn.Text("outcome") == "completed"), string.Join(",", healthy.Select(r => r.Turn.Text("outcome")).Distinct())),
                new LoadCheck("a failed conversation recovers on its next turn", faulty.All(r => r.After.Text("outcome") == "completed"), string.Join(",", faulty.Select(r => r.After.Text("outcome")).Distinct())),
            ]);
    }

    private static async Task<(string Conversation, string Approval, string Operation, Timed Proposal)> ProposeAsync(LoadClient client, Uri url, string message, CancellationToken cancellationToken)
    {
        var conversation = (await client.CreateConversationAsync(url, cancellationToken).ConfigureAwait(false)).Text("conversation_id")!;
        var proposal = await client.TurnAsync(url, conversation, message, null, cancellationToken).ConfigureAwait(false);
        return (conversation, proposal.Text("pending_approval", "approval_id") ?? "", proposal.Text("pending_approval", "operation_id") ?? "", proposal);
    }
}
