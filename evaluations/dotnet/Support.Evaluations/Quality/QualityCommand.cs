using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Microsoft.Extensions.AI;

namespace Support.Evaluations.Quality;

/// <summary>
/// <c>quality --live [--config FILE] [--out DIR] [--source-commit SHA]</c>: the explicitly invoked, billed lane.
/// Without <c>--live</c> it refuses to start, so no CI job or casual run can spend money. ANTHROPIC_API_KEY comes from
/// the environment, goes to the judge client and the launched backends, and is never printed.
/// </summary>
internal static class QualityCommand
{
    public static async Task<int> MainAsync(string[] args)
    {
        string? configPath = null, output = null, sourceCommit = null;
        var live = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--live": live = true; break;
                case "--config": configPath = Path.GetFullPath(Value()); break;
                case "--out": output = Value(); break;
                case "--source-commit": sourceCommit = Value(); break;
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        if (!live)
        {
            throw new ArgumentException("The quality lane calls a real model and is billed: pass --live to run it. CI never does.");
        }

        var config = QualityConfig.Load(configPath ?? QualityConfig.DefaultPath());
        var worstCase = config.WorstCaseCalls();
        if (worstCase > config.Budget.MaxModelCalls)
        {
            throw new ArgumentException($"The configured lane could make {worstCase} model calls, over its budget of {config.Budget.MaxModelCalls}. Reduce cases, repetitions, metrics, or the per-turn cap.");
        }

        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Set ANTHROPIC_API_KEY for the live quality lane.");
        }

        var plan = QualityLane.Plan(config, [.. DatasetLoader.AllPaths().Select(DatasetLoader.Load)]);
        var outDirectory = output ?? Path.Combine(RepositoryRoot.Find(), "evaluations", "results");
        Directory.CreateDirectory(outDirectory);
        Console.WriteLine($"quality: {plan.Count} samples, worst case {worstCase} of {config.Budget.MaxModelCalls} model calls, judge {config.Judge.Model}, backends {string.Join(",", config.Backends.Names)} on {config.Backends.Model}");

        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true; // finish cleanly: running samples stop, the rest are recorded as cancelled
            cancel.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        var anthropic = new AnthropicClient { ApiKey = key };
        using var judge = new BudgetedJudgeClient(anthropic.AsIChatClient(config.Judge.Model, config.Judge.MaxOutputTokens));
        await using var replies = new LiveReplySource(RepositoryRoot.Find(), new LiveModel(config.Backends.Model, key, config.Backends.MaxModelCallsPerTurn));
        try
        {
            var document = await QualityLane.RunAsync(config, plan, replies, judge, GroundingContext.Load(), sourceCommit ?? "unknown", TimeProvider.System, cancel.Token).ConfigureAwait(false);
            var file = Path.Combine(outDirectory, "quality-live.json");
            await File.WriteAllTextAsync(file, document.ToJsonString(EvaluationJson.Options)).ConfigureAwait(false);
            Print(document, file);
            var summary = document["summary"]!;
            var calibrationFailed = summary["calibration"]!.AsArray().Any(c => c!["result"]!.GetValue<string>() != "passed");
            return summary["error"]!.GetValue<int>() > 0 || calibrationFailed ? 1 : 0;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static void Print(JsonObject document, string file)
    {
        var summary = document["summary"]!;
        var budget = document["run"]!["budget"]!;
        Console.WriteLine($"quality: {summary["scored"]} scored, {summary["error"]} error, {summary["skipped"]} skipped; {budget["reserved_calls"]} of {budget["max_model_calls"]} calls reserved ({budget["judge_calls"]} judge calls) -> {file}");
        foreach (var (backend, metrics) in summary["backends"]!.AsObject())
        {
            Console.WriteLine($"  {backend}: {string.Join(", ", metrics!.AsObject().Select(m => $"{m.Key} mean {m.Value!["mean"]?.ToJsonString() ?? "-"} (n={m.Value["scored"]})"))}");
        }

        foreach (var check in summary["calibration"]!.AsArray())
        {
            Console.WriteLine($"  calibration {check!["metric"]}: {check["high"]} {check["high_score"]?.ToJsonString() ?? "-"} vs {check["low"]} {check["low_score"]?.ToJsonString() ?? "-"} -> {check["result"]}");
        }

        foreach (var sample in document["samples"]!.AsArray().Where(s => s!["status"]!.GetValue<string>() != "scored"))
        {
            Console.WriteLine($"  {sample!["status"]} {sample["sample_id"]}: {sample["error"]?.GetValue<string>() ?? sample["skip_reason"]?.GetValue<string>() ?? string.Join("; ", sample["metrics"]!.AsArray().Select(m => m!["error"]?.GetValue<string>()).OfType<string>())}");
        }
    }
}

/// <summary>
/// Live replies from launched backends: one process per customer per backend (shared, read-only store), started on
/// first use in live mode with the per-turn model-call cap. Each question gets a new conversation.
/// </summary>
internal sealed class LiveReplySource(string root, LiveModel live) : IReplySource
{
    private readonly Dictionary<string, Task<ProcessSession>> sessions = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly DirectoryInfo work = Directory.CreateTempSubdirectory("support-quality-");

    public async Task<BackendReply> AskAsync(string backend, string customer, string message, CancellationToken cancellationToken)
    {
        Task<ProcessSession> starting;
        lock (gate)
        {
            if (!sessions.TryGetValue(backend, out starting!))
            {
                var store = Path.Combine(work.FullName, backend, "support.db");
                Directory.CreateDirectory(Path.GetDirectoryName(store)!);
                starting = ProcessSession.StartAsync(backend, root, store, CancellationToken.None, live);
                sessions[backend] = starting;
            }
        }

        var session = await starting.ConfigureAwait(false);
        var baseUrl = session.Principals.TryGetValue(customer, out var url) ? url : throw new InvalidDataException($"no {backend} process for {customer}");
        using var created = await http.PostAsync(new Uri(baseUrl, "api/v1/conversations"), Json(new JsonObject()), cancellationToken).ConfigureAwait(false);
        created.EnsureSuccessStatusCode();
        using var conversation = JsonDocument.Parse(await created.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var id = conversation.RootElement.GetProperty("conversation_id").GetString();

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, $"api/v1/conversations/{id}/turns"))
        {
            Content = Json(new JsonObject { ["client_turn_id"] = Guid.NewGuid().ToString(), ["message"] = message }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var turn = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var result = turn.RootElement;
        static string? Read(JsonElement element, params string[] path)
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

        return new BackendReply(Read(result, "outcome"), Read(result, "reply", "text"), Read(result, "route", "target"), Read(result, "error", "code"));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var starting in sessions.Values)
        {
            try
            {
                await (await starting.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // It never started; nothing to stop.
            }
        }

        http.Dispose();
        try
        {
            work.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A database file still closing; the temp folder is cleaned by the OS.
        }
    }

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");
}
