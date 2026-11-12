using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Support.Evaluations;

/// <summary>What one run selects and allows.</summary>
/// <param name="Backend">dotnet or python: recorded in the run, and used to launch processes in launch mode.</param>
/// <param name="Splits">Which dataset splits run; the rest are skipped with a reason.</param>
/// <param name="StepTimeout">
/// The deadline for one step, stream included (default 3 minutes; the backends' own turn deadline is 90 s). Streams are
/// read as they arrive, so HttpClient.Timeout covers only the headers: without this, a backend that stalls mid-stream
/// would hang the run (Codex review on PR #103). A step past it makes the case an error, and the run goes on.
/// </param>
internal sealed record RunOptions(string Backend, IReadOnlySet<string> Splits, TimeSpan? StepTimeout = null)
{
    public static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromMinutes(3);
}

/// <summary>
/// Runs one case through the public API: a new conversation, each step streamed (so tool calls
/// are visible), and every expected fact checked. Model wording is only a content check, never evidence of what
/// happened; state comes from the operation record (get_operation).
/// </summary>
internal sealed class CaseRunner(Func<Uri, HttpClient> clientFor, IBackends backends, RunOptions options)
{
    private const string EventStream = "text/event-stream";

    public async Task<JsonObject> RunAsync(EvaluationCase item, CancellationToken cancellationToken)
    {
        var skip = SkipReason(item);
        if (skip is not null)
        {
            return CaseResult(item, "skipped", 0, [], skipReason: skip);
        }

        JsonArray steps = [];
        try
        {
            await using var session = await backends.SessionForAsync(item, cancellationToken).ConfigureAwait(false);
            var missing = item.Steps.Select(step => step.AsPrincipal).Append(item.Principal).OfType<string>()
                .Distinct(StringComparer.Ordinal).Where(customer => !session.Principals.ContainsKey(customer)).ToList();
            if (missing.Count > 0)
            {
                return CaseResult(item, "skipped", 0, [], skipReason: $"no backend process for {string.Join(", ", missing)}");
            }

            HttpClient As(string customer) => clientFor(session.Principals[customer]);
            var conversation = await CreateConversationAsync(As(item.Principal), cancellationToken).ConfigureAwait(false);
            Observed? previous = null;
            JsonObject? lastTurnRequest = null;
            string? lastOperation = null;
            for (var index = 0; index < item.Steps.Count; index++)
            {
                var step = item.Steps[index];
                var stepClock = Stopwatch.StartNew();
                if (step.Action == "restart")
                {
                    await session.RestartAsync(cancellationToken).ConfigureAwait(false);
                    steps.Add(StepResult(index, step.Action, 0, stepClock.Elapsed.TotalMilliseconds, Observed.None, []));
                    continue;
                }

                // The step builds on an earlier one. If that one already failed, the case failed (not a runner error).
                if (((step.Action == "decide" && previous?.PendingApproval is null) || (step.Action == "get_operation" && lastOperation is null))
                    && steps.Any(done => done!["checks"]!.AsArray().Any(check => !check!["passed"]!.GetValue<bool>())))
                {
                    return CaseResult(item, "failed", ApiTime(steps), steps);
                }

                var client = As(step.AsPrincipal ?? item.Principal);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(options.StepTimeout ?? RunOptions.DefaultStepTimeout);
                var stepToken = deadline.Token;
                var (status, observed) = step.Action switch
                {
                    "send" => await StreamedAsync(client, $"api/v1/conversations/{conversation}/turns", lastTurnRequest = TurnRequest(step.Message!), step.CancelAfterStart == true, conversation, stepToken).ConfigureAwait(false),
                    "resend_previous" => await StreamedAsync(client, $"api/v1/conversations/{conversation}/turns", lastTurnRequest ?? throw new InvalidDataException("resend_previous needs an earlier send."), false, conversation, stepToken).ConfigureAwait(false),
                    "decide" => await DecideAsync(client, conversation, previous?.PendingApproval ?? throw new InvalidDataException("decide needs a pending approval from an earlier step."), step.Decision!, stepToken).ConfigureAwait(false),
                    "send_raw" => await SendRawAsync(client, conversation, step.RawBody!.Value, stepToken).ConfigureAwait(false),
                    "get_operation" => await GetOperationAsync(client, lastOperation ?? throw new InvalidDataException("get_operation needs an operation from an earlier step."), stepToken).ConfigureAwait(false),
                    _ => throw new InvalidDataException($"Unknown action {step.Action}."),
                };
                var checks = Checks.Compare(step.Expect!, status, observed, previous);
                steps.Add(StepResult(index, step.Action, status, stepClock.Elapsed.TotalMilliseconds, observed, checks));
                lastOperation = observed.OperationId ?? lastOperation;
                // A decision's turn has no pending approval of its own: keep the one it decided, for a repeated decision.
                previous = observed with { PendingApproval = observed.PendingApproval ?? previous?.PendingApproval };
            }

            var passed = steps.All(step => step!["checks"]!.AsArray().All(check => check!["passed"]!.GetValue<bool>()));
            return CaseResult(item, passed ? "passed" : "failed", ApiTime(steps), steps);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CaseResult(item, "error", ApiTime(steps), steps, error: $"A step took longer than {(options.StepTimeout ?? RunOptions.DefaultStepTimeout).TotalSeconds:0.#} s and was stopped.");
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException or InvalidOperationException or IOException)
        {
            // A safe description only: never a response body or stack trace.
            return CaseResult(item, "error", ApiTime(steps), steps, error: $"{error.GetType().Name}: the case could not be run.");
        }
    }

    /// <summary>
    /// A case's latency is the time its requests took: process startup for a fresh store and restart steps are setup,
    /// not API time, so they don't count (they stay visible in the restart step's own latency).
    /// </summary>
    private static double ApiTime(JsonArray steps) =>
        steps.Where(step => step!["action"]!.GetValue<string>() != "restart").Sum(step => step!["latency_ms"]!.GetValue<double>());

    private string? SkipReason(EvaluationCase item)
    {
        if (!options.Splits.Contains(item.Split))
        {
            return $"split '{item.Split}' not selected";
        }

        if (item.Steps.Any(step => step.Action == "restart") && !backends.CanRestart)
        {
            return "restarts the backend: run with --launch so the runner owns the processes";
        }

        if (item.Writes && !backends.IsolatesWrites)
        {
            return "writes simulated state: needs an isolated store (--launch, or --allow-writes for a fresh store)";
        }

        return null;
    }

    private static JsonObject TurnRequest(string message) => new() { ["client_turn_id"] = Guid.NewGuid().ToString(), ["message"] = message };

    private static async Task<string> CreateConversationAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync(new Uri("api/v1/conversations", UriKind.Relative), Json(new JsonObject()), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return body.RootElement.GetProperty("conversation_id").GetString()!;
    }

    private static Task<(int, Observed)> DecideAsync(HttpClient client, string conversation, PendingApproval pending, string decision, CancellationToken cancellationToken) =>
        StreamedAsync(
            client,
            $"api/v1/conversations/{conversation}/approvals/{pending.ApprovalId}",
            new JsonObject { ["operation_id"] = pending.OperationId, ["decision"] = decision },
            false,
            conversation,
            cancellationToken);

    private static async Task<(int, Observed)> SendRawAsync(HttpClient client, string conversation, JsonElement body, CancellationToken cancellationToken)
    {
        using var content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri($"api/v1/conversations/{conversation}/turns", UriKind.Relative), content, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return ((int)response.StatusCode, response.IsSuccessStatusCode ? Observed.FromTurn(document.RootElement, []) : Observed.FromProblem(document.RootElement));
    }

    /// <summary>The authoritative state of a write: the operation record, read back from the API.</summary>
    private static async Task<(int, Observed)> GetOperationAsync(HttpClient client, string operationId, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(new Uri($"api/v1/operations/{operationId}", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return ((int)response.StatusCode, response.IsSuccessStatusCode ? Observed.FromOperation(document.RootElement) : Observed.FromProblem(document.RootElement));
    }

    /// <summary>
    /// A turn or decision as Server-Sent Events, read as they arrive: tool calls come from turn.progress, the result from
    /// turn.final. With <paramref name="cancelAfterStart"/>, the turn is cancelled as soon as turn.started names it.
    /// </summary>
    private static async Task<(int, Observed)> StreamedAsync(HttpClient client, string path, JsonObject body, bool cancelAfterStart, string conversation, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = Json(body) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(EventStream));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentType?.MediaType != EventStream)
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return ((int)response.StatusCode, Observed.FromProblem(problem.RootElement));
        }

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        List<string> tools = [];
        StringBuilder data = new();
        var cancelled = false;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data.Append(line.AsSpan(6));
                continue;
            }

            if (line.Length > 0 || data.Length == 0)
            {
                continue; // an event/id field or a keepalive comment; the event ends at its blank line
            }

            using var streamEvent = JsonDocument.Parse(data.ToString());
            data.Clear();
            var root = streamEvent.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "turn.started" when cancelAfterStart && !cancelled:
                    var turnId = root.GetProperty("turn_id").GetString();
                    using (var cancel = await client.PostAsync(new Uri($"api/v1/conversations/{conversation}/turns/{turnId}/cancel", UriKind.Relative), null, cancellationToken).ConfigureAwait(false))
                    {
                        cancelled = cancel.IsSuccessStatusCode;
                    }

                    break;
                case "turn.progress" when root.GetProperty("data").TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.String:
                    tools.Add(tool.GetString()!);
                    break;
                case "turn.final":
                    return ((int)response.StatusCode, Observed.FromTurn(root.GetProperty("data").GetProperty("result"), tools) with { Cancelled = cancelAfterStart ? cancelled : null });
            }
        }

        throw new InvalidDataException("The stream ended without a turn.final event.");
    }

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static JsonObject StepResult(int index, string action, int status, double latency, Observed observed, IReadOnlyList<Check> checks) => new()
    {
        ["index"] = index,
        ["action"] = action,
        ["http_status"] = status == 0 ? 200 : status,
        ["latency_ms"] = Math.Round(latency, 1),
        ["observed"] = observed.ToJson(),
        ["checks"] = new JsonArray([.. checks.Select(check => check.ToJson())]),
    };

    private static JsonObject CaseResult(EvaluationCase item, string status, double latency, JsonArray steps, string? skipReason = null, string? error = null)
    {
        var result = new JsonObject
        {
            ["case_id"] = item.CaseId,
            ["split"] = item.Split,
            ["status"] = status,
            ["latency_ms"] = Math.Round(latency, 1),
            ["steps"] = steps,
            // The contract doesn't expose token usage, so it is unknown here, never zero.
            ["usage"] = new JsonObject { ["available"] = false },
        };
        if (skipReason is not null)
        {
            result["skip_reason"] = skipReason;
        }

        if (error is not null)
        {
            result["error"] = error;
        }

        return result;
    }
}

internal sealed record PendingApproval(string ApprovalId, string OperationId);

/// <summary>The observable facts of one step's response.</summary>
internal sealed record Observed(
    string? Outcome,
    string? ErrorCode,
    string? ProblemCode,
    string? RouteStatus,
    string? RouteTarget,
    string? UnroutedReason,
    string? ReplyKind,
    string? OperationKind,
    string? OperationStatus,
    string? TurnId,
    string? OperationId,
    IReadOnlyList<string> Tools,
    PendingApproval? PendingApproval,
    string Text,
    string Raw,
    JsonElement? OperationResult = null,
    bool? Cancelled = null)
{
    public static readonly Observed None = new(null, null, null, null, null, null, null, null, null, null, null, [], null, "", "{}");

    private static string? Read(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    public static Observed FromTurn(JsonElement result, IReadOnlyList<string> tools)
    {
        PendingApproval? pending = null;
        string? operationId = null, operationKind = null, operationStatus = null;
        if (result.TryGetProperty("pending_approval", out var approval) && approval.ValueKind == JsonValueKind.Object)
        {
            pending = new PendingApproval(Read(approval, "approval_id")!, Read(approval, "operation_id")!);
            operationId = pending.OperationId;
            operationKind = Read(approval, "kind");
        }

        if (result.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array)
        {
            var operation = operations.EnumerateArray().FirstOrDefault(op => operationId is null || Read(op, "operation_id") == operationId);
            if (operation.ValueKind == JsonValueKind.Object)
            {
                operationId ??= Read(operation, "operation_id");
                operationKind ??= Read(operation, "kind");
                operationStatus = Read(operation, "status");
            }
        }

        return new Observed(
            Read(result, "outcome"),
            Read(result, "error", "code"),
            null,
            Read(result, "route", "status"),
            Read(result, "route", "target"),
            Read(result, "route", "unrouted_reason"),
            Read(result, "reply", "kind"),
            operationKind,
            operationStatus,
            Read(result, "turn_id"),
            operationId,
            tools,
            pending,
            Read(result, "reply", "text") ?? "",
            result.GetRawText());
    }

    public static Observed FromOperation(JsonElement operation) => None with
    {
        OperationKind = Read(operation, "kind"),
        OperationStatus = Read(operation, "status"),
        OperationId = Read(operation, "operation_id"),
        OperationResult = operation.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object ? result.Clone() : null,
        Raw = operation.GetRawText(),
    };

    public static Observed FromProblem(JsonElement problem) => None with
    {
        ProblemCode = problem.TryGetProperty("code", out var code) ? code.GetString() : null,
        Raw = problem.GetRawText(),
    };

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["outcome"] = Outcome,
            ["error_code"] = ErrorCode,
            ["problem_code"] = ProblemCode,
            ["route_status"] = RouteStatus,
            ["route_target"] = RouteTarget,
            ["unrouted_reason"] = UnroutedReason,
            ["reply_kind"] = ReplyKind,
            ["operation_kind"] = OperationKind,
            ["operation_status"] = OperationStatus,
            ["turn_id"] = TurnId,
            ["operation_id"] = OperationId,
            ["tools"] = new JsonArray([.. Tools.Select(tool => (JsonNode?)tool)]),
            ["operation_result"] = OperationResult is { } result ? JsonNode.Parse(result.GetRawText()) : null,
        };
        if (Cancelled is { } cancelled)
        {
            json["cancelled"] = cancelled;
        }

        return json;
    }
}
