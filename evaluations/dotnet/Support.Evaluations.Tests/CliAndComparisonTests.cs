using System.Text.Json.Nodes;

namespace Support.Evaluations.Tests;

/// <summary>The run options that would make a run unsafe or ambiguous are refused, and the comparison flags disagreement (L16).</summary>
public sealed class CliAndComparisonTests
{
    [Theory]
    [InlineData("--backend dotnet")]
    [InlineData("--backend dotnet --launch --principal CUST-1001=http://localhost:5180")]
    [InlineData("--backend dotnet --launch --allow-writes")]
    [InlineData("--backend dotnet --launch --model-mode live")]
    [InlineData("--backend java --launch")]
    [InlineData("--backend dotnet --launch --split training")]
    [InlineData("--backend dotnet --principal CUST-1001")]
    public void Unsafe_or_ambiguous_options_are_refused(string arguments)
    {
        Assert.Throws<ArgumentException>(() => Options.Parse(arguments.Split(' ')));
    }

    [Fact]
    public void Launch_mode_defaults_to_the_development_split_in_fake_mode()
    {
        var options = Options.Parse(["--backend", "python", "--launch"]);
        Assert.True(options.Launch);
        Assert.Equal(["development"], options.Run.Splits);
        Assert.Equal("fake", options.Identity.ModelMode);
    }

    [Fact]
    public void The_comparison_counts_agreement_and_flags_a_case_that_differs()
    {
        var dotnet = Run("dotnet", ("a", "passed", 10), ("b", "passed", 30));
        var python = Run("python", ("a", "passed", 20), ("b", "failed", 40));
        var markdown = Comparison.Render([dotnet, python]);

        Assert.Contains("1 of 2 cases have the same status on dotnet and python", markdown, StringComparison.Ordinal);
        Assert.Contains("| support-x | b | development | passed | 30 | failed | 40 | **no** |", markdown, StringComparison.Ordinal);
        Assert.Contains("| support-x | a | development | passed | 10 | passed | 20 | yes |", markdown, StringComparison.Ordinal);
        Assert.Contains("unknown on every case", markdown, StringComparison.Ordinal);
    }

    private static JsonObject Run(string backend, params (string Case, string Status, double Ms)[] cases)
    {
        var run = DatasetTests.ValidRun();
        run["run"]!["backend"] = backend;
        run["run"]!["dataset"]!["dataset_id"] = "support-x";
        run["run"]!["configuration"] = new JsonObject { ["mode"] = "launch" };
        run["results"] = new JsonArray([.. cases.Select(c => (JsonNode?)new JsonObject
        {
            ["case_id"] = c.Case, ["split"] = "development", ["status"] = c.Status, ["latency_ms"] = c.Ms,
            ["steps"] = new JsonArray(), ["usage"] = new JsonObject { ["available"] = false },
        })]);
        run["summary"] = new JsonObject
        {
            ["total"] = cases.Length,
            ["passed"] = cases.Count(c => c.Status == "passed"),
            ["failed"] = cases.Count(c => c.Status == "failed"),
            ["error"] = 0,
            ["skipped"] = 0,
        };
        return run;
    }
}
