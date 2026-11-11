using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Support.Evaluations;

/// <summary>What one run is allowed to do, and where each customer's backend process listens.</summary>
/// <param name="Principals">
/// Customer → backend base URL. A backend serves one development customer per process, so a case that acts as another
/// customer needs that customer's process too (both on the same store). A case whose customer has no URL is skipped.
/// </param>
/// <param name="AllowWrites">
/// Write cases change simulated state. They run only against an isolated store the caller promises is fresh; otherwise
/// they are skipped, so a run never replays real writes on a shared store.
/// </param>
internal sealed record RunOptions(
    string Backend,
    IReadOnlyDictionary<string, Uri> Principals,
    IReadOnlySet<string> Splits,
    bool AllowWrites);

/// <summary>
/// Runs one case through the public API (L29): it creates a conversation, sends each step (streamed, so tool calls are
/// visible), and checks observable facts against the expectation. Model wording is only a content check, never evidence
/// of what happened.
/// </summary>
internal sealed class CaseRunner(Func<Uri, HttpClient> clientFor, RunOptions options)
{
    private const string EventStream = "text/event-stream";

    public async Task<JsonObject> RunAsync(EvaluationCase item, CancellationToken cancellationToken)
    {
        var skip = SkipReason(item);
        if (skip is not null)
        {
            return CaseResult(item, "skipped", 0, [], skipReason: skip);
        }

        var clock = Stopwatch.StartNew();
        JsonArray steps = [];
        try
        {
            var owner = clientFor(options.Principals[item.Principal]);
            var conversation = await CreateConversationAsync(owner, cancellationToken).ConfigureAwait(false);
            Observed? previous = null;
            JsonObject? lastTurnRequest = null;
            for (var index = 0; index < item.Steps.Count; index++)
            {
                var step = item.Steps[index];
                var client = step.AsPrincipal is null ? owner : clientFor(options.Principals[step.AsPrincipal]);
                var stepClock = Stopwatch.StartNew();
                var (status, observed) = step.Action switch
                {
                    "send" => await SendAsync(client, conversation, lastTurnRequest = TurnRequest(step.Message!), cancellationToken).ConfigureAwait(false),
                    "resend_previous" => await SendAsync(client, conversation, lastTurnRequest ?? throw new InvalidDataException("resend_previous needs an earlier send."), cancellationToken).ConfigureAwait(false),
                    "decide" => await DecideAsync(client, conversation, previous?.PendingApproval ?? throw new InvalidDataException("decide needs a pending approval from an earlier step."), step.Decision!, cancellationToken).ConfigureAwait(false),
                    "send_raw" => await SendRawAsync(client, conversation, step.RawBody!.Value, cancellationToken).ConfigureAwait(false),
                    _ => throw new InvalidDataException($"Unknown action {step.Action}."),
                };
                var checks = Checks.Compare(step.Expect, status, observed, previous);
                steps.Add(StepResult(index, step.Action, status, stepClock.Elapsed.TotalMilliseconds, observed, checks));
                // A decision step's turn has no pending approval of its own; keep the one it decided for a repeat decision.
                previous = observed with { PendingApproval = observed.PendingApproval ?? (step.Action == "decide" ? previous?.PendingApproval : null) };
            }

            var passed = steps.All(step => step!["checks"]!.AsArray().All(check => check!["passed"]!.GetValue<bool>()));
            return CaseResult(item, passed ? "passed" : "failed", clock.Elapsed.TotalMilliseconds, steps);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException or TaskCanceledException)
        {
            // A safe description only: never a response body or stack trace.
            return CaseResult(item, "error", clock.Elapsed.TotalMilliseconds, steps, error: $"{error.GetType().Name}: the case could not be run.");
        }
    }

    private string? SkipReason(EvaluationCase item)
    {
        if (!options.Splits.Contains(item.Split))
        {
            return $"split '{item.Split}' not selected";
        }

        if (item.Writes && !options.AllowWrites)
        {
            return "writes simulated state: needs an isolated store (pass --allow-writes only for a fresh store)";
        }

        var needed = item.Steps.Select(step => step.AsPrincipal).Append(item.Principal).OfType<string>().Distinct(StringComparer.Ordinal);
        var missing = needed.Where(customer => !options.Principals.ContainsKey(customer)).ToList();
        return missing.Count > 0 ? $"no backend process for {string.Join(", ", missing)}" : null;
    }

    private static JsonObject TurnRequest(string message) => new() { ["client_turn_id"] = Guid.NewGuid().ToString(), ["message"] = message };

    private static async Task<string> CreateConversationAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync(new Uri("api/v1/conversations", UriKind.Relative), Json(new JsonObject()), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return body.RootElement.GetProperty("conversation_id").GetString()!;
    }

    private static Task<(int, Observed)> SendAsync(HttpClient client, string conversation, JsonObject request, CancellationToken cancellationToken) =>
        StreamedAsync(client, $"api/v1/conversations/{conversation}/turns", request, cancellationToken);

    private static Task<(int, Observed)> DecideAsync(HttpClient client, string conversation, PendingApproval pending, string decision, CancellationToken cancellationToken) =>
        StreamedAsync(
            client,
            $"api/v1/conversations/{conversation}/approvals/{pending.ApprovalId}",
            new JsonObject { ["operation_id"] = pending.OperationId, ["decision"] = decision },
            cancellationToken);

    private static async Task<(int, Observed)> SendRawAsync(HttpClient client, string conversation, JsonElement body, CancellationToken cancellationToken)
    {
        using var content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri($"api/v1/conversations/{conversation}/turns", UriKind.Relative), content, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(text);
        return ((int)response.StatusCode, response.IsSuccessStatusCode ? Observed.FromTurn(document.RootElement, []) : Observed.FromProblem(document.RootElement));
    }

    /// <summary>A turn or decision as Server-Sent Events: tool calls come from turn.progress, the result from turn.final.</summary>
    private static async Task<(int, Observed)> StreamedAsync(HttpClient client, string path, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = Json(body) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(EventStream));
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentType?.MediaType != EventStream)
        {
            using var problem = JsonDocument.Parse(text);
            return ((int)response.StatusCode, Observed.FromProblem(problem.RootElement));
        }

        List<string> tools = [];
        JsonElement? final = null;
        foreach (var block in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var data = string.Join("\n", block.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => line[6..]));
            if (data.Length == 0)
            {
                continue; // a keepalive comment
            }

            using var streamEvent = JsonDocument.Parse(data);
            var root = streamEvent.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "turn.progress" && root.GetProperty("data").TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.String)
            {
                tools.Add(tool.GetString()!);
            }
            else if (type == "turn.final")
            {
                final = root.GetProperty("data").GetProperty("result").Clone();
            }
        }

        return final is { } result
            ? ((int)response.StatusCode, Observed.FromTurn(result, tools))
            : throw new InvalidDataException("The stream ended without a turn.final event.");
    }

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static JsonObject StepResult(int index, string action, int status, double latency, Observed observed, IReadOnlyList<Check> checks) => new()
    {
        ["index"] = index,
        ["action"] = action,
        ["http_status"] = status,
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
            // The contract doesn't expose token usage, so it is unknown here, never zero (L40/L42).
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
    string Raw)
{
    public static Observed FromTurn(JsonElement result, IReadOnlyList<string> tools)
    {
        string? Read(JsonElement element, params string[] path)
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

    public static Observed FromProblem(JsonElement problem) => new(
        null,
        null,
        problem.TryGetProperty("code", out var code) ? code.GetString() : null,
        null, null, null, null, null, null, null, null,
        [],
        null,
        "",
        problem.GetRawText());

    public JsonObject ToJson() => new()
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
    };
}
