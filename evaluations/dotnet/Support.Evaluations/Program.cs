using System.Text.Json;
using System.Text.Json.Nodes;
using Support.Evaluations;

// The evaluation runner's command line (L29). Two commands:
//
//   validate                         check every dataset in evaluations/datasets against its schema
//   run --backend dotnet|python      run datasets against a running backend and write one result file per dataset
//       --principal CUST-1001=URL    a backend process for each customer the cases act as (repeatable)
//       --dataset PATH               a dataset file (repeatable; default: all)
//       --split development,heldout  which splits to run (default: development)
//       --allow-writes               run write cases: only against a fresh, isolated store
//       --out DIR                    where to write results (default: evaluations/results, which Git ignores)
//       --source-commit SHA          the backend's commit (default: unknown)
//       --model-mode fake|recorded|live, --model-id ID
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
                _ => Usage(),
            };
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or FormatException)
        {
            await Console.Error.WriteLineAsync(error.Message).ConfigureAwait(false);
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: validate | run --backend dotnet|python --principal CUST-1001=http://localhost:5180 [options]");
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
        using var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        Dictionary<Uri, HttpClient> clients = [];
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

            var runner = new CaseRunner(ClientFor, parsed.Run);
            var failed = false;
            foreach (var dataset in datasets)
            {
                var run = await EvaluationRun.RunAsync(dataset, runner, parsed.Run, parsed.Identity, TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
                var summary = run["summary"]!.AsObject();
                failed |= summary["failed"]!.GetValue<int>() > 0 || summary["error"]!.GetValue<int>() > 0;
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

            return failed ? 1 : 0;
        }
        finally
        {
            foreach (var client in clients.Values)
            {
                client.Dispose();
            }
        }
    }
}

internal sealed record Options(RunOptions Run, RunIdentity Identity, IReadOnlyList<string> Datasets, string Out)
{
    public static Options Parse(string[] args)
    {
        string? backend = null, sourceCommit = null, modelMode = null, modelId = null, output = null;
        Dictionary<string, Uri> principals = new(StringComparer.Ordinal);
        List<string> datasets = [];
        HashSet<string> splits = new(StringComparer.Ordinal);
        var allowWrites = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--backend": backend = Value(); break;
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

        if (principals.Count == 0)
        {
            throw new ArgumentException("At least one --principal CUSTOMER=URL is required.");
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
            new RunOptions(backend, principals, splits, allowWrites),
            new RunIdentity(sourceCommit ?? "unknown", modelMode ?? "fake", modelId),
            datasets,
            output ?? Path.Combine(RepositoryRoot.Find(), "evaluations", "results"));
    }
}
