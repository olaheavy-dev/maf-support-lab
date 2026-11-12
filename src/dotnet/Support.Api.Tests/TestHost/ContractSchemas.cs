using System.Text.Json;
using Json.Schema;

namespace Support.Api.Tests.TestHost;

/// <summary>
/// Validates API output against the shared contract schemas (contracts/schemas), the same files
/// the Python backend will be checked against.
/// </summary>
internal static class ContractSchemas
{
    private const string BaseUri = "https://contracts.maf-support-lab.example/v1/";
    private static readonly Lazy<(BuildOptions Options, Dictionary<string, JsonSchema> Schemas)> Loaded = new(Load);

    /// <summary>Whether the shared contract schema accepts the document (no assertion).</summary>
    public static bool IsValid(string schemaName, JsonElement document) =>
        Loaded.Value.Schemas[schemaName].Evaluate(document).IsValid;

    public static void AssertValid(string schemaName, JsonElement document)
    {
        var schema = Loaded.Value.Schemas[schemaName];
        var result = schema.Evaluate(document, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!result.IsValid)
        {
            var errors = (result.Details ?? [])
                .Where(detail => detail.Errors is { Count: > 0 })
                .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}"));
            Assert.Fail($"Response does not match {schemaName}.schema.json:\n{string.Join('\n', errors)}\n{document}");
        }
    }

    private static (BuildOptions, Dictionary<string, JsonSchema>) Load()
    {
        var directory = Path.Combine(RepositoryRoot(), "contracts", "schemas");
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        var files = Directory.GetFiles(directory, "*.schema.json").OrderBy(path => path.Contains("common", StringComparison.Ordinal) ? 0 : 1);
        var schemas = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file).Replace(".schema.json", string.Empty, StringComparison.Ordinal);
            schemas[name] = JsonSchema.FromFile(file, options, new Uri(BaseUri + Path.GetFileName(file)));
        }

        return (options, schemas);
    }

    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "maf-support-lab.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
