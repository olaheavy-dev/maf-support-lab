using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Support.Evaluations.Load;

namespace Support.Evaluations.Tests;

/// <summary>
/// The load scenarios' checks against a misbehaving fake API: each check must fail when the backend leaks,
/// duplicates, or lets two decisions win, and pass when it behaves. (The real backends are exercised by the
/// <c>load</c> command itself; these prove the checks can fail at all.)
/// </summary>
public sealed partial class LoadTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Uri Base = new("http://fake/");

    [Fact]
    public async Task Isolation_passes_when_every_reply_names_only_its_own_order()
    {
        var result = await LoadScenarios.IsolationAsync(Client(new FakeApi()), Principals, 4, 12, None);

        Assert.True(result.Passed, Failures(result));
        Assert.Equal(4 + 12, result.Requests.Count); // 4 conversations created, 3 turns each
    }

    [Fact]
    public async Task Isolation_fails_when_a_reply_names_another_order()
    {
        var result = await LoadScenarios.IsolationAsync(Client(new FakeApi { LeakOrder = "ORD-2002" }), Principals, 4, 8, None);

        Assert.False(Check(result, "no reply names another order").Passed);
    }

    [Fact]
    public async Task Isolation_fails_when_a_turn_comes_back_for_another_conversation()
    {
        var result = await LoadScenarios.IsolationAsync(Client(new FakeApi { WrongConversation = true }), Principals, 2, 4, None);

        Assert.False(Check(result, "every turn belongs to the conversation that sent it").Passed);
    }

    [Fact]
    public async Task Duplicate_submission_fails_when_copies_create_separate_turns()
    {
        Assert.True((await LoadScenarios.DuplicateSubmissionAsync(Client(new FakeApi()), Base, 8, None)).Passed);

        var result = await LoadScenarios.DuplicateSubmissionAsync(Client(new FakeApi { IgnoreClientTurnId = true }), Base, 8, None);
        Assert.False(Check(result, "all copies name one turn").Passed);
    }

    [Fact]
    public async Task Duplicate_approval_fails_when_the_write_repeats_behind_the_same_turn()
    {
        Assert.True((await LoadScenarios.DuplicateApprovalAsync(Client(new FakeApi()), Base, 8, None)).Passed);

        // Every approval writes a ticket, yet every response replays the same resumed turn and the operation says
        // committed: only the probe ticket's number can reveal it.
        var result = await LoadScenarios.DuplicateApprovalAsync(Client(new FakeApi { RepeatWrite = true }), Base, 8, None);
        Assert.True(Check(result, "every response is the same resumed turn").Passed);
        Assert.False(Check(result, "the write ran exactly once (the next ticket is TKT-0002)").Passed);
    }

    [Fact]
    public async Task Decision_race_fails_when_both_decisions_succeed()
    {
        Assert.True((await LoadScenarios.DecisionRaceAsync(Client(new FakeApi()), Base, 4, None)).Passed);

        var result = await LoadScenarios.DecisionRaceAsync(Client(new FakeApi { EveryDecisionWins = true }), Base, 4, None);
        Assert.False(Check(result, "exactly one decision wins").Passed);
    }

    [Fact]
    public void Latency_percentiles_throughput_and_counts_are_computed_from_the_requests()
    {
        var requests = Enumerable.Range(1, 100).Select(ms => new Timed(ms <= 90 ? 200 : 409, new JsonObject { ["outcome"] = ms <= 90 ? "completed" : null, ["code"] = ms > 90 ? "turn_in_progress" : null }, ms)).ToList();
        var json = new ScenarioResult("s", "p", 4, requests, 2000, [new LoadCheck("c", true, "")]).ToJson();

        Assert.Equal(50, json["latency_ms"]!["p50"]!.GetValue<double>());
        Assert.Equal(95, json["latency_ms"]!["p95"]!.GetValue<double>());
        Assert.Equal(100, json["latency_ms"]!["max"]!.GetValue<double>());
        Assert.Equal(50, json["throughput_rps"]!.GetValue<double>()); // 100 requests in 2 s
        Assert.Equal(90, json["http_status"]!["200"]!.GetValue<int>());
        Assert.Equal(10, json["outcomes"]!["turn_in_progress"]!.GetValue<int>());
    }

    [Fact]
    public void A_failed_check_fails_its_scenario_and_the_document_matches_its_schema()
    {
        ScenarioResult[] results =
        [
            new("ok", "p", 1, [new Timed(200, null, 5)], 10, [new LoadCheck("fine", true, "")]),
            new("bad", "p", 1, [new Timed(200, null, 5)], 10, [new LoadCheck("broken", false, "a leak")]),
        ];

        var document = LoadCommand.Document("dotnet", "unknown", [1], 4, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, results); // throws if the schema is broken

        Assert.Equal(1, document["summary"]!["failed"]!.GetValue<int>());
        Assert.Equal("failed", document["scenarios"]![1]!["status"]!.GetValue<string>());
        Assert.False(document["run"]!["cost"]!["available"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_comparison_and_the_report_leave_load_documents_out()
    {
        var folder = Directory.CreateTempSubdirectory("load-compare-");
        try
        {
            var load = LoadCommand.Document("dotnet", "unknown", [1], 4, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [new ScenarioResult("s", "p", 1, [new Timed(200, null, 1)], 1, [new LoadCheck("c", true, "")])]);
            await File.WriteAllTextAsync(Path.Combine(folder.FullName, "load-dotnet.json"), load.ToJsonString(), None);

            Assert.Equal(0, await Cli.MainAsync(["compare", "--results", folder.FullName, "--out", Path.Combine(folder.FullName, "comparison.md")]));
            var (scenarios, read) = await Support.Evaluations.Reporting.ReportCommand.LoadAsync(folder.FullName, "exec", None);
            Assert.Empty(scenarios);
            Assert.Contains(read, line => line.StartsWith("load-dotnet.json: not a result document", StringComparison.Ordinal));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static readonly IReadOnlyDictionary<string, Uri> Principals = new Dictionary<string, Uri> { ["CUST-1001"] = Base, ["CUST-1002"] = Base };

    private static LoadClient Client(FakeApi api) => new(new HttpClient(api));

    private static LoadCheck Check(ScenarioResult result, string name) => result.Checks.Single(c => c.Name == name);

    private static string Failures(ScenarioResult result) => string.Join("; ", result.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}"));

    [GeneratedRegex("ORD-[0-9]{4}")]
    private static partial Regex OrderId();

    /// <summary>A tiny contract API: correct by default, with switches that make it misbehave in one way each.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, string> turnsByClientId = [];
        private readonly Dictionary<string, (string? Decision, string? Ticket)> operations = [];
        private int tickets;
        private int next;

        public string? LeakOrder { get; init; }

        public bool WrongConversation { get; init; }

        public bool IgnoreClientTurnId { get; init; }

        public bool EveryDecisionWins { get; init; }

        public bool RepeatWrite { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
            lock (gate)
            {
                var id = ++next;
                if (path == "/api/v1/conversations")
                {
                    return Json(201, new JsonObject { ["conversation_id"] = $"conv_{id}" });
                }

                var segments = path.Split('/');
                var conversation = segments[4];
                if (path.EndsWith("/turns", StringComparison.Ordinal))
                {
                    var message = body!["message"]!.GetValue<string>();
                    if (message.Contains("ticket", StringComparison.OrdinalIgnoreCase))
                    {
                        operations[$"op_{id}"] = (null, null);
                        return Json(200, new JsonObject { ["outcome"] = "awaiting_approval", ["conversation_id"] = conversation, ["turn_id"] = $"turn_{id}", ["pending_approval"] = new JsonObject { ["approval_id"] = $"appr_{id}", ["operation_id"] = $"op_{id}" } });
                    }

                    var clientId = body["client_turn_id"]!.GetValue<string>();
                    if (!IgnoreClientTurnId && turnsByClientId.TryGetValue(clientId, out var existing))
                    {
                        return Json(200, new JsonObject { ["outcome"] = "completed", ["conversation_id"] = conversation, ["turn_id"] = existing });
                    }

                    turnsByClientId[clientId] = $"turn_{id}";
                    var asked = OrderId().Match(message).Value;
                    return Json(200, new JsonObject
                    {
                        ["outcome"] = "completed",
                        ["conversation_id"] = WrongConversation ? "conv_other" : conversation,
                        ["turn_id"] = $"turn_{id}",
                        ["reply"] = new JsonObject { ["kind"] = "answer", ["text"] = $"Order {asked} has shipped.{(LeakOrder is null ? "" : $" Also {LeakOrder}.")}" },
                    });
                }

                if (path.Contains("/approvals/", StringComparison.Ordinal))
                {
                    var operation = body!["operation_id"]!.GetValue<string>();
                    var decision = body["decision"]!.GetValue<string>();
                    var (decided, ticket) = operations[operation];
                    var first = decided is null;
                    decided ??= decision;
                    if (decision == "approve" && (first || RepeatWrite || EveryDecisionWins) && decided == "approve" | EveryDecisionWins)
                    {
                        var written = $"TKT-{++tickets:0000}"; // a repeated write takes another number
                        ticket ??= written;
                    }

                    operations[operation] = (decided, ticket);
                    return EveryDecisionWins || decided == decision
                        ? Json(200, new JsonObject { ["outcome"] = "completed", ["turn_id"] = $"turn_resumed_{operation}" })
                        : Json(409, new JsonObject { ["code"] = "approval_already_decided" });
                }

                if (path.StartsWith("/api/v1/operations/", StringComparison.Ordinal))
                {
                    var (decided, ticket) = operations[segments[4]];
                    return Json(200, new JsonObject { ["status"] = decided == "approve" ? "committed" : "rejected", ["result"] = new JsonObject { ["ticket_id"] = ticket } });
                }

                return Json(404, new JsonObject { ["code"] = "not_found" });
            }
        }

        private static HttpResponseMessage Json(int status, JsonObject body) =>
            new((HttpStatusCode)status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
