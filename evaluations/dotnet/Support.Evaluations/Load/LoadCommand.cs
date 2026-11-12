using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Support.Evaluations.Load;

/// <summary>
/// <c>load --backend dotnet|python [--levels 1,4,16] [--turns-per-level 96] [--out DIR] [--source-commit SHA]</c>: fault and
/// bounded-load scenarios against one backend that the runner launches itself, in fake model mode (no key, no cost).
/// Read-only scenarios share one store; every scenario that writes gets a fresh store and fresh processes. Writes one
/// evaluation-load document; exits 1 if any check failed. Live load is out of scope: a live model would measure the
/// provider, and the per-turn model-call cap already bounds what one turn can cost.
/// </summary>
internal static class LoadCommand
{
    public const string SchemaVersion = "1.0";

    public static async Task<int> MainAsync(string[] args)
    {
        string? backend = null, output = null, sourceCommit = null;
        int[] levels = [1, 4, 16];
        var turns = 96;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--backend": backend = Value(); break;
                case "--levels": levels = [.. Value().Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture))]; break;
                case "--turns-per-level": turns = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--out": output = Value(); break;
                case "--source-commit": sourceCommit = Value(); break;
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        if (backend is not ("dotnet" or "python"))
        {
            throw new ArgumentException("--backend must be dotnet or python.");
        }

        if (levels.Length == 0 || levels.Any(l => l is < 1 or > 64) || turns is < 1 or > 500)
        {
            throw new ArgumentException("--levels must be 1-64 each, and --turns-per-level 1-500 (bounded load).");
        }

        var root = RepositoryRoot.Find();
        var work = Directory.CreateTempSubdirectory("support-load-");
        using var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), MaxConnectionsPerServer = 128 };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        var client = new LoadClient(http);
        var stores = 0;
        string Store() => Path.Combine(Directory.CreateDirectory(Path.Combine(work.FullName, $"{backend}-{++stores}")).FullName, "support.db");
        var started = DateTimeOffset.UtcNow;
        List<ScenarioResult> results = [];
        try
        {
            var ct = CancellationToken.None;
            await using (var shared = await ProcessSession.StartAsync(backend, root, Store(), ct).ConfigureAwait(false))
            {
                // Warm up once (first-call JIT, connection setup), so level 1 isn't charged for startup.
                await LoadScenarios.IsolationAsync(client, shared.Principals, 2, 1, ct).ConfigureAwait(false);
                foreach (var level in levels)
                {
                    results.Add(await LoadScenarios.IsolationAsync(client, shared.Principals, level, turns, ct).ConfigureAwait(false));
                }

                results.Add(await LoadScenarios.DuplicateSubmissionAsync(client, shared.Principals["CUST-1001"], 16, ct).ConfigureAwait(false));
                results.Add(await LoadScenarios.TurnInProgressAsync(client, shared.Principals["CUST-1001"], 8, ct).ConfigureAwait(false));
                results.Add(await LoadScenarios.FaultsUnderLoadAsync(client, shared.Principals["CUST-1001"], 16, ct).ConfigureAwait(false));
            }

            await using (var fresh = await ProcessSession.StartAsync(backend, root, Store(), ct).ConfigureAwait(false))
            {
                results.Add(await LoadScenarios.DuplicateApprovalAsync(client, fresh.Principals["CUST-1001"], 16, ct).ConfigureAwait(false));
            }

            await using (var fresh = await ProcessSession.StartAsync(backend, root, Store(), ct).ConfigureAwait(false))
            {
                results.Add(await LoadScenarios.DecisionRaceAsync(client, fresh.Principals["CUST-1001"], 8, ct).ConfigureAwait(false));
            }
        }
        finally
        {
            try
            {
                work.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A database file still closing; the temp folder is cleaned by the OS.
            }
        }

        var document = Document(backend, sourceCommit ?? "unknown", levels, turns, started, DateTimeOffset.UtcNow, results);
        var outDirectory = output ?? Path.Combine(root, "evaluations", "results");
        Directory.CreateDirectory(outDirectory);
        var file = Path.Combine(outDirectory, $"load-{backend}.json");
        await File.WriteAllTextAsync(file, document.ToJsonString(EvaluationJson.Options)).ConfigureAwait(false);
        foreach (var scenario in document["scenarios"]!.AsArray())
        {
            var latency = scenario!["latency_ms"]!;
            Console.WriteLine($"{backend} {scenario["name"],-22} c={scenario["concurrency"],-3} n={scenario["requests"],-4} {scenario["throughput_rps"],7} req/s  p50 {latency["p50"]} p95 {latency["p95"]} max {latency["max"]} ms  {scenario["status"]}");
            foreach (var check in scenario["checks"]!.AsArray().Where(c => !c!["passed"]!.GetValue<bool>()))
            {
                Console.WriteLine($"    FAILED {check!["name"]}: {check["detail"]}");
            }
        }

        Console.WriteLine($"-> {file}");
        return results.All(r => r.Passed) ? 0 : 1;
    }

    public static JsonObject Document(string backend, string sourceCommit, int[] levels, int turns, DateTimeOffset started, DateTimeOffset finished, IReadOnlyList<ScenarioResult> results)
    {
        var document = new JsonObject
        {
            ["schema_version"] = SchemaVersion,
            ["run"] = new JsonObject
            {
                ["run_id"] = "load_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                ["started_at"] = started.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                ["finished_at"] = finished.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                ["backend"] = backend,
                ["source_commit"] = sourceCommit,
                ["model"] = new JsonObject { ["mode"] = "fake" },
                ["target"] = new JsonObject
                {
                    ["launch"] = "one process per customer (CUST-1001, CUST-1002), Release build / uv locked, loopback HTTP; the runner and the backend share the machine",
                    ["os"] = RuntimeInformation.OSDescription,
                    ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
                    ["processors"] = Environment.ProcessorCount,
                },
                ["workload"] = new JsonObject
                {
                    ["isolation_levels"] = new JsonArray([.. levels.Select(l => (JsonNode?)l)]),
                    ["turns_per_level"] = turns,
                    ["warm_up"] = "one isolation pass at concurrency 2 before measuring",
                },
                ["cost"] = new JsonObject { ["available"] = false, ["reason"] = "fake model: no provider calls, no token usage, no cost" },
                ["runner"] = new JsonObject { ["name"] = EvaluationRun.RunnerName, ["version"] = EvaluationRun.RunnerVersion },
            },
            ["scenarios"] = new JsonArray([.. results.Select(r => (JsonNode?)r.ToJson())]),
            ["summary"] = new JsonObject
            {
                ["scenarios"] = results.Count,
                ["passed"] = results.Count(r => r.Passed),
                ["failed"] = results.Count(r => !r.Passed),
            },
        };
        var problems = EvaluationSchemas.Errors(EvaluationSchemas.LoadRun, document);
        return problems.Count == 0 ? document : throw new InvalidOperationException($"The load document breaks its schema:\n  {string.Join("\n  ", problems)}");
    }
}
