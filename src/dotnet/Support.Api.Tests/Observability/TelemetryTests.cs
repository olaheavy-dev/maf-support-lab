using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using OpenTelemetry.Metrics;
using Support.Api.Modules.Agents.Infrastructure.ModelProvider;

namespace Support.Api.Tests.Observability;

/// <summary>
/// L06 acceptance: what Aspire receives for a fake-mode success and failure. The tests read the app's real
/// OpenTelemetry pipeline through in-memory exporters, so they check behaviour, not how the code is written.
/// </summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class TelemetryTests
{
    private const string OrderQuestion = "Where is my order ORD-1002?";
    private const string CustomerId = "CUST-1001";

    [Fact]
    public async Task A_turn_is_one_trace_from_HTTP_through_agent_model_and_tool()
    {
        var capture = new TelemetryCapture();
        using var factory = SupportApiFactory.Create(configureServices: capture.AddTo);
        var api = new ApiClient(factory.CreateClient());

        var turn = await api.AskAsync(await api.CreateConversationAsync(), OrderQuestion);
        var spans = await capture.TraceAsync(factory, TraceIdOf(turn));

        var http = Assert.Single(spans, span => span.Kind == ActivityKind.Server);
        var turnSpan = Assert.Single(spans, span => span.Source.Name == "Support.Api" && span.DisplayName == "support.turn");
        var agent = Assert.Single(spans, span => Tag(span, "gen_ai.operation.name") == "invoke_agent");
        var modelCalls = spans.Where(span => span.Source.Name == "Support.Model" && Tag(span, "gen_ai.operation.name") == "chat").ToList();
        var tool = Assert.Single(spans, span => Tag(span, "gen_ai.operation.name") == "execute_tool");

        // Nested and distinguishable: HTTP > support.turn > invoke_agent (Support.Agents) > chat calls and the tool.
        Assert.Equal(http.SpanId, turnSpan.ParentSpanId);
        Assert.Equal(turnSpan.SpanId, agent.ParentSpanId);
        Assert.Equal("Support.Agents", agent.Source.Name);
        Assert.Equal(2, modelCalls.Count);
        Assert.All(modelCalls, call => Assert.Equal(agent.SpanId, call.ParentSpanId));
        Assert.Equal(agent.SpanId, tool.ParentSpanId);
        Assert.Equal("get_order_status", Tag(tool, "gen_ai.tool.name"));

        // Identifiers belong on the turn span, where they correlate with the API response.
        Assert.Equal(turn.GetProperty("conversation_id").GetString(), Tag(turnSpan, "support.conversation.id"));
        Assert.Equal(turn.GetProperty("turn_id").GetString(), Tag(turnSpan, "support.turn.id"));
        Assert.Equal("completed", Tag(turnSpan, "support.turn.outcome"));
        Assert.Equal("answer", Tag(turnSpan, "support.reply_kind"));

        // Logs from the handler and the tool carry the same trace, so Aspire links them to these spans.
        Assert.Contains(capture.Logs, log => log.TraceId == http.TraceId && log.CategoryName!.EndsWith("SubmitTurnCommandHandler", StringComparison.Ordinal));
        Assert.Contains(capture.Logs, log => log.TraceId == http.TraceId && log.CategoryName!.EndsWith("OrderStatusTool", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_DevUI_chat_is_traced_through_the_same_agent_model_and_tool()
    {
        var capture = new TelemetryCapture();
        using var factory = SupportApiFactory.Create(configureServices: capture.AddTo);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/v1/responses", UriKind.Relative),
            new { agent = new { name = "orders" }, input = OrderQuestion },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var agent = await WaitForAsync(() => capture.Spans.FirstOrDefault(span => Tag(span, "gen_ai.operation.name") == "invoke_agent"));
        var spans = await capture.TraceAsync(factory, agent.TraceId);

        // DevUI enters through its own HTTP route but reaches the same instrumented agent instance as the API.
        var http = Assert.Single(spans, span => span.Kind == ActivityKind.Server);
        Assert.Equal("/v1/responses", Tag(http, "url.path"));
        Assert.Equal("orders", Tag(agent, "gen_ai.agent.name"));
        Assert.Equal(2, spans.Count(span => span.Source.Name == "Support.Model" && Tag(span, "gen_ai.operation.name") == "chat"));
        Assert.Single(spans, span => Tag(span, "gen_ai.operation.name") == "execute_tool");

        // No contract-API turn: the support.turn span and turn metrics belong to the conversation handler only.
        Assert.DoesNotContain(spans, span => span.DisplayName == "support.turn");
        var metrics = capture.FlushMetrics(factory);
        Assert.DoesNotContain(metrics, metric => metric.Name == "support.turns");
        Assert.Equal(1, Point(metrics, "Support.Api", "support.tool.calls").GetSumLong());
    }

    [Fact]
    public async Task Metrics_have_samples_with_bounded_labels_and_tokens_are_counted_once()
    {
        var capture = new TelemetryCapture();
        using var factory = SupportApiFactory.Create(configureServices: capture.AddTo);
        var api = new ApiClient(factory.CreateClient());

        var turn = await api.AskAsync(await api.CreateConversationAsync(), OrderQuestion);
        await capture.TraceAsync(factory, TraceIdOf(turn));
        var metrics = capture.FlushMetrics(factory);

        var turns = Point(metrics, "Support.Api", "support.turns");
        Assert.Equal(1, turns.GetSumLong());
        Assert.Equal(
            new Dictionary<string, string> { ["support.agent"] = "orders", ["support.reply_kind"] = "answer", ["support.turn.outcome"] = "completed" },
            Tags(turns));
        Assert.Equal(1, Point(metrics, "Support.Api", "support.turn.duration").GetHistogramCount());
        var toolCalls = Point(metrics, "Support.Api", "support.tool.calls");
        Assert.Equal(1, toolCalls.GetSumLong());
        Assert.Equal(new Dictionary<string, string> { ["support.tool.name"] = "get_order_status", ["support.tool.outcome"] = "found" }, Tags(toolCalls));

        // The fake model reports 100 input tokens per call and this turn makes two calls. Summed over every meter,
        // exactly 200 input tokens must be recorded: the agent layer's re-count of the same tokens is dropped.
        var inputTokens = metrics
            .Where(metric => metric.Name == "gen_ai.client.token.usage")
            .SelectMany(metric => metric.MetricPoints)
            .Where(point => Tags(point).GetValueOrDefault("gen_ai.token.type") == "input")
            .Sum(point => point.GetHistogramSum());
        Assert.Equal(2 * FakeChatClient.FakeInputTokens, inputTokens);
        Assert.DoesNotContain(metrics, metric => metric.MeterName == "Support.Agents" && metric.Name == "gen_ai.client.token.usage");

        // No metric label may carry an id: ids are unbounded and belong on spans and logs.
        string[] ids = [turn.GetProperty("conversation_id").GetString()!, turn.GetProperty("turn_id").GetString()!, TraceIdOf(turn).ToHexString(), CustomerId];
        Assert.All(
            metrics.SelectMany(metric => metric.MetricPoints).SelectMany(point => Tags(point).Values),
            label => Assert.DoesNotContain(label, ids));
    }

    [Fact]
    public async Task A_failed_turn_has_error_status_a_failure_metric_and_a_correlated_error_log()
    {
        var capture = new TelemetryCapture();
        using var factory = SupportApiFactory.Create(configureServices: capture.AddTo);
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        using var response = await api.Http.PostAsJsonAsync(
            new Uri($"/api/v1/conversations/{conversation}/turns", UriKind.Relative),
            new { client_turn_id = Guid.NewGuid().ToString(), message = $"Where is ORD-1002? {FakeChatClient.FailureTrigger}" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var turnSpan = await WaitForAsync(() => capture.Spans.SingleOrDefault(span => span.DisplayName == "support.turn"));
        var spans = await capture.TraceAsync(factory, turnSpan.TraceId);

        Assert.Equal(ActivityStatusCode.Error, turnSpan.Status);
        Assert.Equal("failed", Tag(turnSpan, "support.turn.outcome"));
        Assert.Equal(typeof(InvalidOperationException).FullName, Tag(turnSpan, "error.type"));
        Assert.Contains(turnSpan.Events, e => e.Name == "exception");
        Assert.Equal("500", Tag(Assert.Single(spans, span => span.Kind == ActivityKind.Server), "http.response.status_code"));

        var error = Assert.Single(capture.Logs, log => log.CategoryName!.EndsWith("SubmitTurnCommandHandler", StringComparison.Ordinal));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, error.LogLevel);
        Assert.Equal(turnSpan.TraceId, error.TraceId);
        Assert.IsType<InvalidOperationException>(error.Exception);

        var turns = Point(capture.FlushMetrics(factory), "Support.Api", "support.turns");
        Assert.Equal("failed", Tags(turns)["support.turn.outcome"]);
        Assert.Equal("none", Tags(turns)["support.reply_kind"]);
    }

    [Fact]
    public async Task Message_text_and_customer_id_stay_out_of_telemetry_by_default_even_with_the_GenAI_capture_variable_on()
    {
        // The whole test process runs with the standard capture variable on (see GenAiCaptureVariable).
        Assert.Equal("true", Environment.GetEnvironmentVariable(GenAiCaptureVariable.Name));

        var capture = new TelemetryCapture();
        using var factory = SupportApiFactory.Create(configureServices: capture.AddTo);
        var api = new ApiClient(factory.CreateClient());

        var turn = await api.AskAsync(await api.CreateConversationAsync(), OrderQuestion);
        var spans = await capture.TraceAsync(factory, TraceIdOf(turn));

        Assert.All(TelemetryText(spans, capture), text =>
        {
            Assert.DoesNotContain(OrderQuestion, text, StringComparison.Ordinal);
            Assert.DoesNotContain(CustomerId, text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Sensitive_capture_is_an_explicit_setting()
    {
        var capture = new TelemetryCapture();
        using var factory = SupportApiFactory.Create(
            settings: new Dictionary<string, string?> { ["Telemetry:CaptureSensitiveData"] = "true" },
            configureServices: capture.AddTo);
        var api = new ApiClient(factory.CreateClient());

        var turn = await api.AskAsync(await api.CreateConversationAsync(), OrderQuestion);
        var spans = await capture.TraceAsync(factory, TraceIdOf(turn));

        Assert.Contains(TelemetryText(spans, capture), text => text.Contains(OrderQuestion, StringComparison.Ordinal));
    }

    private static ActivityTraceId TraceIdOf(System.Text.Json.JsonElement turn) =>
        ActivityTraceId.CreateFromString(turn.GetProperty("trace_id").GetString());

    private static string? Tag(Activity span, string name) => span.GetTagItem(name)?.ToString();

    private static MetricPoint Point(IEnumerable<MetricSnapshot> metrics, string meter, string name)
    {
        var metric = Assert.Single(metrics, metric => metric.MeterName == meter && metric.Name == name);
        return Assert.Single(metric.MetricPoints);
    }

    private static Dictionary<string, string> Tags(MetricPoint point)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in point.Tags)
        {
            tags[tag.Key] = tag.Value?.ToString() ?? string.Empty;
        }

        return tags;
    }

    /// <summary>Every piece of text the trace's spans and logs would send to Aspire.</summary>
    private static IEnumerable<string> TelemetryText(IEnumerable<Activity> spans, TelemetryCapture capture)
    {
        var traceIds = spans.Select(span => span.TraceId).ToHashSet();
        foreach (var span in spans)
        {
            yield return span.DisplayName;
            foreach (var tag in span.TagObjects)
            {
                yield return tag.Value?.ToString() ?? string.Empty;
            }

            foreach (var spanEvent in span.Events)
            {
                foreach (var tag in spanEvent.Tags)
                {
                    yield return tag.Value?.ToString() ?? string.Empty;
                }
            }
        }

        foreach (var log in capture.Logs.Where(log => traceIds.Contains(log.TraceId)))
        {
            yield return log.FormattedMessage ?? log.Body ?? string.Empty;
            foreach (var attribute in log.Attributes ?? [])
            {
                yield return attribute.Value?.ToString() ?? string.Empty;
            }
        }
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe)
        where T : class
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (probe() is { } value)
            {
                return value;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The expected telemetry did not arrive.");
    }
}
