using System.Text.Json.Nodes;
using Support.Evaluations;

// The evaluation runner's command line (L29, L16). Three commands:
//
//   validate                         check every dataset in evaluations/datasets against its schema
//   run --backend dotnet|python      run datasets against a backend and write one result file per dataset
//       --launch                     the runner starts the backend itself (fake mode, both customers): a shared store
//                                    for read-only cases, a fresh store per write case, and restarts. Needs the built
//                                    .NET API (Release) or uv for Python.
//       --principal CUST-1001=URL    or: backends started elsewhere, one process per customer (repeatable)
//       --allow-writes               with --principal: run write cases (only against a fresh, isolated store)
//       --dataset PATH               a dataset file (repeatable; default: all)
//       --split development,heldout  which splits to run (default: development)
//       --out DIR                    where to write results (default: evaluations/results, ignored by Git)
//       --source-commit SHA          the backend's commit (default: unknown)
//       --model-mode fake|recorded|live, --model-id ID
//   compare --results DIR --out FILE  a Markdown baseline and cross-backend comparison of the run documents in DIR
//   native record|evaluate [--recordings DIR] [--out DIR]
//                                    native MAF evaluation (L30): record the fake agent's conversations, or score the
//                                    saved recordings without re-running the agent
//   report [--results DIR] [--store DIR] [--execution NAME]
//                                    write the result documents in DIR (default evaluations/results) to a disk result
//                                    store for `dotnet aieval report` (L33). Reads files only: no model, no backend.
//   load --backend dotnet|python [--levels 1,4,16] [--turns-per-level 96] [--out DIR] [--source-commit SHA]
//                                    fault and bounded-load scenarios (L25) on a backend the runner launches (fake mode)
//   quality --live [--config FILE] [--out DIR] [--source-commit SHA]
//                                    the budgeted, model-judged quality lane (L32): live replies from both backends
//                                    judged for relevance and groundedness. Billed, so it never runs without --live.
//
// Exit codes: 0 everything selected passed, 1 a case failed or errored, 2 invalid input.
return await Cli.MainAsync(args).ConfigureAwait(false);

internal static class Cli
{
    public static async Task<int> MainAsync(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "validate" => Validate(),
                "run" => await RunAsync(Options.Parse(args[1..])).ConfigureAwait(false),
                "compare" => await Comparison.WriteAsync(args[1..]).ConfigureAwait(false),
                "native" => await Support.Evaluations.Native.NativeEvaluation.MainAsync(args[1..]).ConfigureAwait(false),
                "report" => await Support.Evaluations.Reporting.ReportCommand.MainAsync(args[1..]).ConfigureAwait(false),
                "load" => await Support.Evaluations.Load.LoadCommand.MainAsync(args[1..]).ConfigureAwait(false),
                "quality" => await Support.Evaluations.Quality.QualityCommand.MainAsync(args[1..]).ConfigureAwait(false),
                _ => Usage(),
            };
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or FormatException)
        {
            await Console.Error.WriteLineAsync(error.Message).ConfigureAwait(false);
            return 2;
        }
    }

    /// <summary>
    /// The run's exit code, the CI gate (L33): 1 if any case failed or errored, otherwise 0. Skipped cases don't fail
    /// the run (they say why), but an error does: a case that couldn't be run is not a pass.
    /// </summary>
    public static int ExitCodeFor(IEnumerable<JsonObject> runs) =>
        runs.Any(run => run["summary"]!["failed"]!.GetValue<int>() > 0 || run["summary"]!["error"]!.GetValue<int>() > 0) ? 1 : 0;

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: validate | run --backend dotnet|python (--launch | --principal CUST-1001=URL) [options] | compare --results DIR --out FILE | native record|evaluate | report | load --backend dotnet|python | quality --live");
        return 2;
    }

    private static int Validate()
    {
        var failures = 0;
        foreach (var path in DatasetLoader.AllPaths())
        {
            try
            {
                var dataset = DatasetLoader.Load(path);
                Console.WriteLine($"{Path.GetFileName(path)}: {dataset.DatasetId} {dataset.DatasetVersion}, {dataset.Cases.Count} cases");
            }
            catch (InvalidDataException error)
            {
                failures++;
                Console.Error.WriteLine(error.Message);
            }
        }

        return failures == 0 ? 0 : 2;
    }

    private static async Task<int> RunAsync(Options parsed)
    {
        var datasets = (parsed.Datasets.Count > 0 ? parsed.Datasets : DatasetLoader.AllPaths()).Select(DatasetLoader.Load).ToList();
        Directory.CreateDirectory(parsed.Out);
        var work = Directory.CreateTempSubdirectory("support-evaluations-");
        using var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        Dictionary<Uri, HttpClient> clients = [];
        IBackends backends = parsed.Launch
            ? new LaunchedBackends(parsed.Run.Backend, RepositoryRoot.Find(), work.FullName)
            : new FixedBackends(parsed.Principals, parsed.AllowWrites);
        try
        {
            HttpClient ClientFor(Uri baseUrl)
            {
                if (!clients.TryGetValue(baseUrl, out var client))
                {
                    client = new HttpClient(handler, disposeHandler: false) { BaseAddress = baseUrl, Timeout = TimeSpan.FromMinutes(3) };
                    clients[baseUrl] = client;
                }

                return client;
            }

            var runner = new CaseRunner(ClientFor, backends, parsed.Run);
            var configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mode"] = parsed.Launch ? "launch" : "endpoint",
                ["isolated_writes"] = backends.IsolatesWrites ? "true" : "false",
                ["customers"] = parsed.Launch ? string.Join(",", LaunchedBackends.Customers) : string.Join(",", parsed.Principals.Keys.Order(StringComparer.Ordinal)),
            };
            List<JsonObject> runs = [];
            foreach (var dataset in datasets)
            {
                var run = await EvaluationRun.RunAsync(dataset, runner, parsed.Run, parsed.Identity, configuration, TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
                var summary = run["summary"]!.AsObject();
                runs.Add(run);
                var file = Path.Combine(parsed.Out, $"{parsed.Run.Backend}-{dataset.DatasetId}.json");
                await File.WriteAllTextAsync(file, run.ToJsonString(EvaluationJson.Options)).ConfigureAwait(false);
                Console.WriteLine($"{parsed.Run.Backend} {dataset.DatasetId}: {summary["passed"]} passed, {summary["failed"]} failed, {summary["error"]} error, {summary["skipped"]} skipped -> {file}");
                foreach (var result in run["results"]!.AsArray().Where(r => r!["status"]!.GetValue<string>() is "failed" or "error"))
                {
                    var differences = result!["steps"]!.AsArray()
                        .SelectMany(step => step!["checks"]!.AsArray().Where(check => !check!["passed"]!.GetValue<bool>())
                            .Select(check => $"step {step!["index"]} {check!["name"]}: expected {check["expected"]?.ToJsonString()}, got {check["actual"]?.ToJsonString()}"));
                    Console.WriteLine($"  {result["status"]} {result["case_id"]}: {string.Join("; ", differences.DefaultIfEmpty(result["error"]?.GetValue<string>() ?? ""))}");
                }
            }

            return ExitCodeFor(runs);
        }
        finally
        {
            await backends.DisposeAsync().ConfigureAwait(false);
            foreach (var client in clients.Values)
            {
                client.Dispose();
            }

            try
            {
                work.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A database file still closing; the temp folder is cleaned by the OS.
            }
        }
    }
}

internal sealed record Options(
    RunOptions Run,
    RunIdentity Identity,
    IReadOnlyList<string> Datasets,
    string Out,
    bool Launch,
    IReadOnlyDictionary<string, Uri> Principals,
    bool AllowWrites)
{
    public static Options Parse(string[] args)
    {
        string? backend = null, sourceCommit = null, modelMode = null, modelId = null, output = null;
        Dictionary<string, Uri> principals = new(StringComparer.Ordinal);
        List<string> datasets = [];
        HashSet<string> splits = new(StringComparer.Ordinal);
        bool allowWrites = false, launch = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--backend": backend = Value(); break;
                case "--launch": launch = true; break;
                case "--principal":
                    var pair = Value().Split('=', 2);
                    if (pair.Length != 2 || !Uri.TryCreate(pair[1].EndsWith('/') ? pair[1] : pair[1] + "/", UriKind.Absolute, out var url))
                    {
                        throw new ArgumentException("--principal takes CUSTOMER=URL, for example CUST-1001=http://localhost:5180.");
                    }

                    principals[pair[0]] = url;
                    break;
                case "--dataset": datasets.Add(Path.GetFullPath(Value())); break;
                case "--split": splits.UnionWith(Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                case "--allow-writes": allowWrites = true; break;
                case "--out": output = Value(); break;
                case "--source-commit": sourceCommit = Value(); break;
                case "--model-mode": modelMode = Value(); break;
                case "--model-id": modelId = Value(); break;
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        if (backend is not ("dotnet" or "python"))
        {
            throw new ArgumentException("--backend must be dotnet or python.");
        }

        if (launch == principals.Count > 0)
        {
            throw new ArgumentException("Use either --launch or at least one --principal CUSTOMER=URL.");
        }

        if (launch && allowWrites)
        {
            throw new ArgumentException("--allow-writes is for --principal: --launch already gives every write case a fresh store.");
        }

        if (launch && modelMode is not (null or "fake"))
        {
            throw new ArgumentException("--launch runs the backends in fake mode only.");
        }

        if (splits.Count == 0)
        {
            splits.Add("development");
        }

        if (splits.Any(split => split is not ("development" or "heldout")))
        {
            throw new ArgumentException("--split takes development and/or heldout.");
        }

        return new Options(
            new RunOptions(backend, splits),
            new RunIdentity(sourceCommit ?? "unknown", modelMode ?? "fake", modelId),
            datasets,
            output ?? Path.Combine(RepositoryRoot.Find(), "evaluations", "results"),
            launch,
            principals,
            allowWrites);
    }
}
