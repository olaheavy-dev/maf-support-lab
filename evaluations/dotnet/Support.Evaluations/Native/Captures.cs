using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Support.Evaluations.Native;

/// <summary>
/// Recorded conversations (evaluations/recordings/*.json, schema evaluation-capture): normalized from MAF's messages when
/// recorded, and turned back into MAF inputs when evaluated. The normalized form is ours, so no SDK's internal
/// serialization is part of the evidence.
/// </summary>
internal static class Captures
{
    private static readonly Lazy<JsonSchema> Schema = new(() =>
        JsonSchema.FromFile(Path.Combine(RepositoryRoot.Find(), "evaluations", "schemas", "evaluation-capture.schema.json")));

    public static string Directory => Path.Combine(RepositoryRoot.Find(), "evaluations", "recordings");

    public static IReadOnlyList<string> Problems(JsonNode capture)
    {
        var result = Schema.Value.Evaluate(JsonSerializer.SerializeToElement(capture), new EvaluationOptions { OutputFormat = OutputFormat.List });
        return result.IsValid
            ? []
            : [.. (result.Details ?? []).Where(d => d.Errors is { Count: > 0 }).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")).Take(10)];
    }

    public static JsonObject Load(string path)
    {
        var capture = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var problems = Problems(capture);
        return problems.Count == 0 ? capture : throw new InvalidDataException($"{Path.GetFileName(path)} is not a valid capture: {problems[0]}");
    }

    /// <summary>Normalizes one evaluated item (query plus the agent's response messages) into a capture.</summary>
    public static JsonObject FromConversation(Dataset dataset, EvaluationCase item, string query, IEnumerable<ChatMessage> responseMessages, IEnumerable<AITool> tools)
    {
        JsonArray messages = [];
        foreach (var message in responseMessages)
        {
            JsonArray contents = [];
            foreach (var content in message.Contents)
            {
                JsonObject? normalized = content switch
                {
                    TextContent { Text.Length: > 0 } text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
                    ToolApprovalRequestContent { ToolCall: FunctionCallContent call } => Call("approval_request", call),
                    FunctionCallContent call => Call("function_call", call),
                    FunctionResultContent result => new JsonObject { ["type"] = "function_result", ["call_id"] = result.CallId, ["result"] = result.Result?.ToString() ?? "" },
                    _ => null,
                };
                if (normalized is not null)
                {
                    contents.Add(normalized);
                }
            }

            if (contents.Count > 0)
            {
                messages.Add(new JsonObject { ["role"] = message.Role == ChatRole.Tool ? "tool" : "assistant", ["contents"] = contents });
            }
        }

        var capture = new JsonObject
        {
            ["schema_version"] = "1.0",
            ["capture_id"] = $"{dataset.DatasetId}-{item.CaseId}",
            ["dataset_id"] = dataset.DatasetId,
            ["dataset_version"] = dataset.DatasetVersion,
            ["case_id"] = item.CaseId,
            ["source"] = "fake-agent",
            ["agent"] = FakeSupportAgent.Name,
            ["principal"] = item.Principal,
            ["tools"] = new JsonArray([.. tools.Select(tool => (JsonNode?)new JsonObject { ["name"] = tool.Name, ["writes"] = FakeSupportAgent.WriteTools.Contains(tool.Name) })]),
            ["context"] = new JsonArray([.. (item.Context ?? []).Select(reference => (JsonNode?)reference)]),
            ["query"] = query,
            ["messages"] = messages,
            ["response_text"] = string.Join("\n", responseMessages.Where(m => m.Role == ChatRole.Assistant).Select(m => m.Text).Where(t => t.Length > 0)),
        };
        var problems = Problems(capture);
        return problems.Count == 0 ? capture : throw new InvalidDataException($"The capture for {item.CaseId} breaks its schema: {problems[0]}");
    }

    /// <summary>The recorded response as MAF's <see cref="AgentResponse"/>: what the recorded-response evaluation scores.</summary>
    public static AgentResponse ToAgentResponse(JsonObject capture)
    {
        List<ChatMessage> messages = [];
        foreach (var message in capture["messages"]!.AsArray())
        {
            List<AIContent> contents = [];
            foreach (var content in message!["contents"]!.AsArray())
            {
                var type = content!["type"]!.GetValue<string>();
                contents.Add(type switch
                {
                    "text" => new TextContent(content["text"]!.GetValue<string>()),
                    // A paused approval is still the model's call (and has no result): checks see it as a function call.
                    "function_call" or "approval_request" => new FunctionCallContent(content["call_id"]!.GetValue<string>(), content["name"]!.GetValue<string>(), Arguments(content["arguments"]!.AsObject())),
                    "function_result" => new FunctionResultContent(content["call_id"]!.GetValue<string>(), content["result"]!.GetValue<string>()),
                    _ => throw new InvalidDataException($"Unknown content type {type}."),
                });
            }

            messages.Add(new ChatMessage(message["role"]!.GetValue<string>() == "tool" ? ChatRole.Tool : ChatRole.Assistant, contents));
        }

        return new AgentResponse(messages);
    }

    /// <summary>
    /// The dataset's expected tool calls, with canonical snake_case argument names mapped to the .NET tools' parameter
    /// names (MEAI keeps C# names: order_id → orderId). Python's tools use snake_case as is (L31).
    /// </summary>
    public static IReadOnlyList<ExpectedToolCall> ExpectedToolCalls(EvaluationCase item) =>
        [.. (item.ExpectedToolCalls ?? []).Select(call => new ExpectedToolCall(
            call.Name,
            call.Arguments is { } arguments ? arguments.EnumerateObject().ToDictionary(p => DotNetName(p.Name), p => (object)JsonToValue(p.Value)) : null))];

    internal static string DotNetName(string canonical)
    {
        var parts = canonical.Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static JsonObject Call(string type, FunctionCallContent call) => new()
    {
        ["type"] = type,
        ["call_id"] = call.CallId,
        ["name"] = call.Name,
        ["arguments"] = JsonSerializer.SerializeToNode(call.Arguments ?? new Dictionary<string, object?>()),
    };

    private static Dictionary<string, object?> Arguments(JsonObject arguments) =>
        arguments.ToDictionary(pair => pair.Key, pair => pair.Value is null ? null : (object?)JsonToValue(JsonSerializer.SerializeToElement(pair.Value)));

    private static object JsonToValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => value.GetRawText(),
    };
}
