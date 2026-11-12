using System.Text.Json;
using System.Text.Json.Nodes;

namespace Support.Evaluations.Tests;

/// <summary>The committed datasets are valid, and malformed or unversioned datasets and results are rejected.</summary>
public sealed class DatasetTests
{
    [Fact]
    public void Every_committed_dataset_is_valid_and_versioned()
    {
        var paths = DatasetLoader.AllPaths();
        Assert.Equal(5, paths.Count);
        var datasets = paths.Select(DatasetLoader.Load).ToList();
        Assert.Equal(["adversarial", "ambiguity", "denial", "failure", "success"], datasets.Select(d => d.Category).Order(StringComparer.Ordinal));
        Assert.All(datasets, d => Assert.Equal("1.2", d.SchemaVersion));
        Assert.Contains(datasets.SelectMany(d => d.Cases), c => c.Split == "heldout");
        Assert.All(datasets.SelectMany(d => d.Cases).Where(c => c.Writes), c => Assert.Equal("fresh_store", c.Reset));
    }

    public static TheoryData<string, string> BrokenDatasets => new()
    {
        { "unversioned", """{"schema_version": null}""" },
        { "unknown split", """{"cases": [{"split": "training"}]}""" },
        { "write case on a shared conversation", """{"cases": [{"writes": true, "reset": "fresh_conversation"}]}""" },
        { "unknown field", """{"extra": 1}""" },
    };

    [Theory]
    [MemberData(nameof(BrokenDatasets))]
    public void A_malformed_dataset_is_rejected(string why, string patch)
    {
        var dataset = Valid();
        Merge(dataset, JsonNode.Parse(patch)!.AsObject());
        Assert.NotEmpty(DatasetLoader.Problems(JsonSerializer.SerializeToElement(dataset)));
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public void A_send_step_without_a_message_is_rejected()
    {
        var dataset = Valid();
        dataset["cases"]![0]!["steps"]![0]!.AsObject().Remove("message");
        Assert.NotEmpty(DatasetLoader.Problems(JsonSerializer.SerializeToElement(dataset)));
    }

    [Fact]
    public void A_step_that_expects_nothing_is_rejected()
    {
        var dataset = Valid();
        dataset["cases"]![0]!["steps"]![0]!["expect"] = new JsonObject();
        Assert.NotEmpty(DatasetLoader.Problems(JsonSerializer.SerializeToElement(dataset)));
    }

    [Fact]
    public void Duplicate_case_ids_are_rejected()
    {
        var dataset = Valid();
        var cases = dataset["cases"]!.AsArray();
        cases.Add(cases[0]!.DeepClone());
        Assert.Contains(DatasetLoader.Problems(JsonSerializer.SerializeToElement(dataset)), p => p.Contains("used more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void The_valid_example_used_by_these_tests_passes()
    {
        Assert.Empty(DatasetLoader.Problems(JsonSerializer.SerializeToElement(Valid())));
    }

    public static TheoryData<string, string> BrokenRuns => new()
    {
        { "unversioned", """{"schema_version": "0.9"}""" },
        { "unknown usage reported as numbers", """{"results": [{"usage": {"available": false, "input_tokens": 0}}]}""" },
        { "skipped without a reason", """{"results": [{"status": "skipped"}]}""" },
        { "error without a description", """{"results": [{"status": "error"}]}""" },
        { "bad run id", """{"run": {"run_id": "run-1"}}""" },
        { "unknown backend", """{"run": {"backend": "java"}}""" },
    };

    [Theory]
    [MemberData(nameof(BrokenRuns))]
    public void A_malformed_run_document_is_rejected(string why, string patch)
    {
        var run = ValidRun();
        Merge(run, JsonNode.Parse(patch)!.AsObject());
        Assert.NotEmpty(EvaluationSchemas.Errors(EvaluationSchemas.Run, run));
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public void The_valid_run_example_passes()
    {
        Assert.Empty(EvaluationSchemas.Errors(EvaluationSchemas.Run, ValidRun()));
    }

    internal static JsonObject Valid() => JsonNode.Parse("""
        {
          "schema_version": "1.0", "dataset_id": "example", "dataset_version": "1.0.0", "category": "success",
          "description": "An example.", "fixture_version": "1.0",
          "live_run": {"eligible": false, "max_cases": 0, "max_model_calls_per_case": 1, "repetitions": 1},
          "cases": [{
            "case_id": "order-status", "split": "development", "principal": "CUST-1001", "purpose": "Known order",
            "source": "new", "writes": false, "reset": "fresh_conversation",
            "steps": [{"action": "send", "message": "Where is ORD-1002?", "expect": {"outcome": "completed", "reply_kind": "answer"}}],
            "expected_state_change": {"type": "none"}
          }]
        }
        """)!.AsObject();

    internal static JsonObject ValidRun() => JsonNode.Parse("""
        {
          "schema_version": "1.0",
          "run": {
            "run_id": "run_0123456789abcdef0123456789abcdef", "started_at": "2026-11-11T10:00:00.000Z",
            "finished_at": "2026-11-11T10:00:01.000Z", "backend": "dotnet", "source_commit": "unknown",
            "model": {"mode": "fake", "model_id": null},
            "dataset": {"dataset_id": "example", "dataset_version": "1.0.0", "splits": ["development"]},
            "scorer": {"name": "contract-facts", "version": "1.0.0"}, "runner": {"name": "support-evaluations", "version": "0.1.0"},
            "configuration": {"allow_writes": false}
          },
          "results": [{"case_id": "order-status", "split": "development", "status": "passed", "latency_ms": 12.5, "steps": [], "usage": {"available": false}}],
          "summary": {"total": 1, "passed": 1, "failed": 0, "error": 0, "skipped": 0}
        }
        """)!.AsObject();

    /// <summary>Applies a partial document: objects merge, arrays merge item by item, anything else replaces.</summary>
    internal static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is JsonObject child && target[key] is JsonObject existing)
            {
                Merge(existing, child);
            }
            else if (value is JsonArray items && target[key] is JsonArray current)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    if (items[i] is JsonObject item && i < current.Count && current[i] is JsonObject into)
                    {
                        Merge(into, item);
                    }
                }
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }
}
