using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Support.Evaluations.Tests;

/// <summary>
/// The data-to-result flow over a fake backend (L29): cases pass on matching facts, fail naming the fact that differed,
/// are skipped when they can't run safely, and every run document matches its schema. The real backends are covered by
/// launch mode in CI (cross-backend job): every dataset on both backends.
/// </summary>
public sealed class RunnerTests
{
    private static readonly Uri Owner = new("http://owner.test/");

    [Fact]
    public async Task A_case_passes_when_every_observed_fact_matches()
    {
        var backend = new FakeBackend(Answer("answer", "orders", ["get_order_status"], "Order ORD-1002 has shipped."));
        var run = await RunAsync(backend, Case(expect: """{"outcome": "completed", "reply_kind": "answer", "route_target": "orders", "tools": ["get_order_status"], "mentions": ["ORD-1002"]}"""));

        var result = Assert.Single(run["results"]!.AsArray())!;
        Assert.Equal("passed", result["status"]!.GetValue<string>());
        Assert.All(result["steps"]![0]!["checks"]!.AsArray(), check => Assert.True(check!["passed"]!.GetValue<bool>()));
        Assert.Equal(["get_order_status"], result["steps"]![0]!["observed"]!["tools"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(1, run["summary"]!["passed"]!.GetValue<int>());
        Assert.False(result["usage"]!["available"]!.GetValue<bool>()); // unknown, never zero
    }

    [Fact]
    public async Task A_case_fails_and_names_the_fact_that_differed()
    {
        var backend = new FakeBackend(Answer("clarification", "orders", [], "Which order?"));
        var run = await RunAsync(backend, Case(expect: """{"reply_kind": "answer", "tools": ["get_order_status"]}"""));

        var result = Assert.Single(run["results"]!.AsArray())!;
        Assert.Equal("failed", result["status"]!.GetValue<string>());
        var failed = result["steps"]![0]!["checks"]!.AsArray().Where(c => !c!["passed"]!.GetValue<bool>()).Select(c => c!["name"]!.GetValue<string>());
        Assert.Equal(["reply_kind", "tools"], failed);
    }

    [Fact]
    public async Task Leaked_data_anywhere_in_the_result_fails_the_case()
    {
        var backend = new FakeBackend(Answer("not_found", "orders", ["get_order_status"], "Not found.", extra: "Mechanical Keyboard"));
        var run = await RunAsync(backend, Case(expect: """{"reply_kind": "not_found", "must_not_mention": ["Mechanical Keyboard"]}"""));
        Assert.Equal("failed", run["results"]![0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_problem_response_is_observed_by_its_status_and_code()
    {
        var backend = new FakeBackend(Problem(400, "invalid_request"));
        var run = await RunAsync(backend, Case(action: "send_raw", expect: """{"http_status": 400, "problem_code": "invalid_request"}"""));
        Assert.Equal("passed", run["results"]![0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Write_cases_are_skipped_unless_the_store_is_isolated()
    {
        var item = Case(writes: true, expect: """{"outcome": "awaiting_approval"}""");
        var skipped = await RunAsync(new FakeBackend(Answer("approval_request", "orders", ["cancel_order"], "Approve?")), item);
        Assert.Equal("skipped", skipped["results"]![0]!["status"]!.GetValue<string>());
        Assert.Contains("isolated store", skipped["results"]![0]!["skip_reason"]!.GetValue<string>(), StringComparison.Ordinal);

        var backend = new FakeBackend(Answer("approval_request", "orders", ["cancel_order"], "Approve?", outcome: "awaiting_approval"));
        var allowed = await RunAsync(backend, item, allowWrites: true);
        Assert.Equal("passed", allowed["results"]![0]!["status"]!.GetValue<string>());
        Assert.Equal("true", allowed["run"]!["configuration"]!["isolated_writes"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_case_needing_a_customer_without_a_backend_process_is_skipped()
    {
        var run = await RunAsync(new FakeBackend(), Case(principal: "CUST-1002", expect: """{"outcome": "completed"}"""));
        Assert.Equal("skipped", run["results"]![0]!["status"]!.GetValue<string>());
        Assert.Equal("no backend process for CUST-1002", run["results"]![0]!["skip_reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_restart_case_is_skipped_when_the_runner_does_not_own_the_processes()
    {
        var item = Case(writes: true, expect: """{"outcome": "awaiting_approval"}""");
        item = item with { Steps = [.. item.Steps, new CaseStep("restart", null, null, null, null, null)] };
        var run = await RunAsync(new FakeBackend(), item, allowWrites: true);
        Assert.Contains("--launch", run["results"]![0]!["skip_reason"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restart_step_restarts_the_session_and_later_steps_use_its_new_address()
    {
        var restarting = new RestartingBackends();
        var backend = new FakeBackend(
            Answer("approval_request", "orders", ["cancel_order"], "Approve?", outcome: "awaiting_approval"),
            Answer("approval_result", "orders", [], "Done.", operationStatus: "committed"));
        var item = Case(writes: true, expect: """{"outcome": "awaiting_approval"}""");
        item = item with
        {
            Steps =
            [
                .. item.Steps,
                new CaseStep("restart", null, null, null, null, null),
                new CaseStep("decide", null, "approve", null, null, Expect("""{"operation_status": "committed"}""")),
            ],
        };
        var run = await RunAsync(backend, item, backends: restarting);

        Assert.Equal("passed", run["results"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(1, restarting.Restarts);
        Assert.Equal("restart", run["results"]![0]!["steps"]![1]!["action"]!.GetValue<string>());
        Assert.Equal("true", run["run"]!["configuration"]!["isolated_writes"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_operation_record_is_checked_with_a_result_subset()
    {
        var backend = new FakeBackend(
            Answer("approval_request", "orders", ["cancel_order"], "Approve?", outcome: "awaiting_approval"),
            Answer("approval_result", "orders", [], "Done.", operationStatus: "committed"),
            () => "JSON" + """{"operation_id": "op_0123456789abcdef", "kind": "cancel_order", "status": "committed", "result": {"order_id": "ORD-1003", "order_status": "cancelled"}}""");
        var item = Case(writes: true, expect: """{"outcome": "awaiting_approval"}""");
        item = item with
        {
            Steps =
            [
                .. item.Steps,
                new CaseStep("decide", null, "approve", null, null, Expect("""{"operation_status": "committed"}""")),
                new CaseStep("get_operation", null, null, null, null, Expect("""{"http_status": 200, "operation_status": "committed", "operation_result": {"order_status": "cancelled"}}""")),
            ],
        };
        var passed = await RunAsync(backend, item, allowWrites: true);
        Assert.Equal("passed", passed["results"]![0]!["status"]!.GetValue<string>());
        Assert.EndsWith("/api/v1/operations/op_0123456789abcdef", backend.Paths[^1], StringComparison.Ordinal);

        var wrong = item with { Steps = [.. item.Steps.Take(2), new CaseStep("get_operation", null, null, null, null, Expect("""{"operation_result": {"order_status": "processing"}}"""))] };
        var failed = await RunAsync(new FakeBackend(
            Answer("approval_request", "orders", ["cancel_order"], "Approve?", outcome: "awaiting_approval"),
            Answer("approval_result", "orders", [], "Done.", operationStatus: "committed"),
            () => "JSON" + """{"operation_id": "op_0123456789abcdef", "status": "committed", "result": {"order_status": "cancelled"}}"""), wrong, allowWrites: true);
        Assert.Equal("failed", failed["results"]![0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_step_that_depends_on_a_failed_step_fails_the_case_instead_of_erroring()
    {
        var backend = new FakeBackend(Answer("answer", "orders", [], "Shipped."));
        var item = Case(writes: true, expect: """{"outcome": "awaiting_approval"}""");
        item = item with { Steps = [.. item.Steps, new CaseStep("decide", null, "approve", null, null, Expect("""{"operation_status": "committed"}"""))] };
        var run = await RunAsync(backend, item, allowWrites: true);
        Assert.Equal("failed", run["results"]![0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Cancel_after_start_posts_a_cancel_for_the_turn_the_stream_named()
    {
        var backend = new FakeBackend(Answer("answer", "orders", [], "", outcome: "cancelled"));
        var item = Case(expect: """{"outcome": "cancelled"}""");
        item = item with { Steps = [item.Steps[0] with { CancelAfterStart = true }] };
        var run = await RunAsync(backend, item);

        Assert.Equal("passed", run["results"]![0]!["status"]!.GetValue<string>());
        Assert.Contains(backend.Paths, path => path.EndsWith("/turns/turn_started0000000/cancel", StringComparison.Ordinal));
        Assert.True(run["results"]![0]!["steps"]![0]!["observed"]!["cancelled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Heldout_cases_run_only_when_their_split_is_selected()
    {
        var run = await RunAsync(new FakeBackend(), Case(split: "heldout", expect: """{"outcome": "completed"}"""));
        Assert.Equal("skipped", run["results"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(["development"], run["run"]!["dataset"]!["splits"]!.AsArray().Select(s => s!.GetValue<string>()));
    }

    [Fact]
    public async Task An_unreachable_backend_is_a_safe_error_not_a_crash()
    {
        var backend = new FakeBackend { Unreachable = true };
        var run = await RunAsync(backend, Case(expect: """{"outcome": "completed"}"""));
        var result = run["results"]![0]!;
        Assert.Equal("error", result["status"]!.GetValue<string>());
        Assert.Equal("HttpRequestException: the case could not be run.", result["error"]!.GetValue<string>());
        Assert.Equal(1, run["summary"]!["error"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_decision_answers_the_approval_from_the_step_before_it()
    {
        var backend = new FakeBackend(
            Answer("approval_request", "orders", ["cancel_order"], "Approve?", outcome: "awaiting_approval"),
            Answer("approval_result", "orders", [], "Done.", operationStatus: "committed"));
        var item = Case(writes: true, expect: """{"outcome": "awaiting_approval", "operation_kind": "cancel_order"}""");
        var steps = new List<CaseStep>(item.Steps)
        {
            new("decide", null, "approve", null, null, new Expectation(null, null, "completed", null, null, null, null, "approval_result", null, "committed", null, null, null, null, null, null)),
        };
        var run = await RunAsync(backend, item with { Steps = steps }, allowWrites: true);

        Assert.Equal("passed", run["results"]![0]!["status"]!.GetValue<string>());
        Assert.EndsWith("/approvals/appr_0123456789abcdef", backend.Paths[^1], StringComparison.Ordinal);
    }

    private static async Task<JsonObject> RunAsync(FakeBackend backend, EvaluationCase item, bool allowWrites = false, IBackends? backends = null)
    {
        using var client = new HttpClient(backend) { BaseAddress = Owner };
        var options = new RunOptions("dotnet", new HashSet<string> { "development" });
        backends ??= new FixedBackends(new Dictionary<string, Uri> { ["CUST-1001"] = Owner }, allowWrites);
        var dataset = new Dataset("1.1", "example", "1.1.0", "success", "An example.", "1.0", new LiveRun(false, 0, 1, 1), [item]);
        var configuration = new Dictionary<string, string> { ["mode"] = "endpoint", ["isolated_writes"] = backends.IsolatesWrites ? "true" : "false" };
        return await EvaluationRun.RunAsync(dataset, new CaseRunner(_ => client, backends, options), options, new RunIdentity("unknown", "fake", null), configuration, TimeProvider.System, CancellationToken.None);
    }

    private static Expectation Expect(string json) => JsonSerializer.Deserialize<Expectation>(json, EvaluationJson.Options)!;

    private static EvaluationCase Case(string expect, string action = "send", bool writes = false, string principal = "CUST-1001", string split = "development")
    {
        var expectation = JsonSerializer.Deserialize<Expectation>(expect, EvaluationJson.Options)!;
        var step = action == "send_raw"
            ? new CaseStep("send_raw", null, null, JsonSerializer.SerializeToElement(new { message = "no id" }), null, expectation)
            : new CaseStep("send", "Where is ORD-1002?", null, null, null, expectation);
        return new EvaluationCase("order-status", split, principal, "An example.", "new", writes, writes ? "fresh_store" : "fresh_conversation", null, [step], new StateChange("none", null));
    }

    private static Func<string> Answer(string kind, string target, string[] tools, string text, string outcome = "completed", string? extra = null, string? operationStatus = null) => () =>
    {
        var result = new JsonObject
        {
            ["contract_version"] = "1.1",
            ["turn_id"] = "turn_" + Guid.NewGuid().ToString("N")[..16],
            ["outcome"] = outcome,
            ["route"] = new JsonObject { ["status"] = "routed", ["target"] = target },
            ["reply"] = new JsonObject { ["kind"] = kind, ["text"] = text + (extra is null ? "" : $" ({extra})") },
            ["operations"] = new JsonArray(),
        };
        if (outcome == "awaiting_approval")
        {
            result["pending_approval"] = new JsonObject { ["approval_id"] = "appr_0123456789abcdef", ["operation_id"] = "op_0123456789abcdef", ["kind"] = "cancel_order" };
            result["operations"] = new JsonArray(new JsonObject { ["operation_id"] = "op_0123456789abcdef", ["kind"] = "cancel_order", ["status"] = "awaiting_approval" });
        }

        if (operationStatus is not null)
        {
            result["operations"] = new JsonArray(new JsonObject { ["operation_id"] = "op_0123456789abcdef", ["kind"] = "cancel_order", ["status"] = operationStatus });
        }

        StringBuilder stream = new();
        stream.Append("event: turn.started\ndata: {\"type\":\"turn.started\",\"sequence\":1,\"turn_id\":\"turn_started0000000\"}\n\n: keepalive\n\n");
        foreach (var tool in tools)
        {
            stream.Append(CultureInvariant($"event: turn.progress\ndata: {{\"type\":\"turn.progress\",\"data\":{{\"stage\":\"tool_call\",\"tool\":\"{tool}\"}}}}\n\n"));
        }

        stream.Append("event: turn.final\ndata: ").Append(new JsonObject { ["type"] = "turn.final", ["data"] = new JsonObject { ["result"] = result } }.ToJsonString()).Append("\n\n");
        return "SSE" + stream;
    };

    private static Func<string> Problem(int status, string code) => () => $"PROBLEM{status}" + new JsonObject { ["code"] = code, ["status"] = status }.ToJsonString();

    private static string CultureInvariant(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>A launched-style provider over the fake backend: writes are isolated, and restarts are counted.</summary>
    private sealed class RestartingBackends : IBackends, IBackendSession
    {
        public int Restarts { get; private set; }

        public bool IsolatesWrites => true;

        public bool CanRestart => true;

        public IReadOnlyDictionary<string, Uri> Principals => new Dictionary<string, Uri> { ["CUST-1001"] = Owner };

        public Task<IBackendSession> SessionForAsync(EvaluationCase item, CancellationToken cancellationToken) => Task.FromResult<IBackendSession>(this);

        public Task RestartAsync(CancellationToken cancellationToken)
        {
            Restarts++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Answers conversation creation, then each turn or decision with the next scripted response.</summary>
    private sealed class FakeBackend(params Func<string>[] responses) : HttpMessageHandler
    {
        private int next;

        public bool Unreachable { get; init; }

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Unreachable)
            {
                throw new HttpRequestException("connection refused");
            }

            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/api/v1/conversations")
            {
                return Task.FromResult(Respond(HttpStatusCode.Created, "application/json", """{"conversation_id":"conv_0123456789abcdef"}"""));
            }

            if (path.EndsWith("/cancel", StringComparison.Ordinal))
            {
                return Task.FromResult(Respond(HttpStatusCode.Accepted, "application/json", """{"cancel_requested":true}"""));
            }

            var body = responses.Length == 0 ? Answer("answer", "orders", [], "ok")() : responses[Math.Min(next++, responses.Length - 1)]();
            if (body.StartsWith("SSE", StringComparison.Ordinal))
            {
                return Task.FromResult(Respond(HttpStatusCode.OK, "text/event-stream", body[3..]));
            }

            if (body.StartsWith("JSON", StringComparison.Ordinal))
            {
                return Task.FromResult(Respond(HttpStatusCode.OK, "application/json", body[4..]));
            }

            var status = int.Parse(body.AsSpan(7, 3), System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(Respond((HttpStatusCode)status, "application/problem+json", body[10..]));
        }

        private static HttpResponseMessage Respond(HttpStatusCode status, string type, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, type) };
    }
}
