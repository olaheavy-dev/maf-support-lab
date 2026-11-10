using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Support.Api.Modules.Agents.Infrastructure.ModelProvider;

namespace Support.Api.Tests.Modules.Streaming;

/// <summary>
/// L12: a turn can be streamed as Server-Sent Events (contract 1.1). Every event matches the schema, the stream starts
/// with turn.started and ends with exactly one turn.final, the reply text after the last reset equals the final reply,
/// and the final result is the same stored turn a status lookup returns.
/// </summary>
public sealed class StreamingTests
{
    [Fact]
    public async Task An_answer_streams_progress_and_token_deltas_then_one_final_event()
    {
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        var (status, contentType, events, _) = await SseClient.PostAsync(api.Http, $"/api/v1/conversations/{conversation}/turns", new { client_turn_id = Guid.NewGuid().ToString(), message = "Where is my order ORD-1002?" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("text/event-stream", contentType);
        var final = SseClient.Final(events);
        Assert.Equal("completed", final.GetProperty("outcome").GetString());
        Assert.Equal("answer", final.ReplyKind());
        var stages = events.Where(e => e.GetProperty("type").GetString() == "turn.progress").Select(e => e.GetProperty("data").GetProperty("stage").GetString()).ToList();
        Assert.Equal(["triage", "routed", "specialist", "tool_call"], stages.Distinct());
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "turn.progress" && e.GetProperty("data").TryGetProperty("tool", out var tool) && tool.GetString() == FakeChatClient.OrderStatusToolName);
        // Token streaming: the reply arrived in several pieces, before the final event, with no reset needed.
        Assert.True(events.Count(e => e.GetProperty("type").GetString() == "reply.delta") > 1);
        Assert.DoesNotContain("reply.reset", SseClient.Types(events));
        Assert.Equal(final.ReplyText(), SseClient.CountedText(events));
        // The final event is the stored turn: a status lookup returns the same result.
        var (_, stored) = await api.SendAsync(HttpMethod.Get, $"/api/v1/conversations/{conversation}/turns/{final.GetProperty("turn_id").GetString()}");
        Assert.Equal(final.GetRawText(), stored.GetRawText());
    }

    [Fact]
    public async Task Text_the_model_streamed_before_asking_for_approval_is_reset_and_the_pause_is_not_success()
    {
        // The model says it will cancel, then calls the write tool: the native stream sends that text before the approval
        // request. The server's reply is its own approval request, so the client is told to discard the model's words.
        var model = new CountingScript(messages => messages[^1].Contents.OfType<FunctionResultContent>().Any()
            ? FakeChatClient.DefaultScript(messages)
            : new ChatResponse(new ChatMessage(ChatRole.Assistant, [
                new TextContent("Sure, I will cancel ORD-1003 right away."),
                new FunctionCallContent("call_1", FakeChatClient.CancelOrderToolName, new Dictionary<string, object?> { ["orderId"] = "ORD-1003" })])));
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001", chatClient: model.Client());
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        var (_, _, events, _) = await SseClient.PostAsync(api.Http, $"/api/v1/conversations/{conversation}/turns", new { client_turn_id = Guid.NewGuid().ToString(), message = "Please cancel order ORD-1003." });

        var final = SseClient.Final(events);
        Assert.Equal("awaiting_approval", final.GetProperty("outcome").GetString()); // a pause, not business completion
        var types = SseClient.Types(events).ToList();
        Assert.True(types.IndexOf("reply.delta") < types.IndexOf("reply.reset"));
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "turn.progress" && e.GetProperty("data").GetProperty("stage").GetString() == "awaiting_approval");
        Assert.Equal(final.ReplyText(), SseClient.CountedText(events));
        Assert.DoesNotContain("right away", SseClient.CountedText(events), StringComparison.Ordinal);
        Assert.Equal("processing", Order(factory, "ORD-1003"));
    }

    [Fact]
    public async Task A_streamed_approval_shows_the_operation_running_and_ends_with_the_committed_result()
    {
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var pending = (await api.AskAsync(conversation, "Please cancel order ORD-1003.")).GetProperty("pending_approval");

        var (_, _, events, _) = await SseClient.PostAsync(
            api.Http,
            $"/api/v1/conversations/{conversation}/approvals/{pending.GetProperty("approval_id").GetString()}",
            new { operation_id = pending.GetProperty("operation_id").GetString(), decision = "approve" });

        var final = SseClient.Final(events);
        Assert.Equal(JsonValueKind.Null, events[0].GetProperty("data").GetProperty("client_turn_id").ValueKind);
        Assert.Equal(final.GetProperty("resumed_from_turn_id").GetString(), events[0].GetProperty("data").GetProperty("resumed_from_turn_id").GetString());
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "turn.progress" && e.GetProperty("data").GetProperty("stage").GetString() == "executing_operation");
        Assert.Equal("approval_result", final.ReplyKind());
        Assert.Equal("committed", Assert.Single(final.GetProperty("operations").EnumerateArray()).GetProperty("status").GetString());
        Assert.Equal(final.ReplyText(), SseClient.CountedText(events));
        Assert.Equal("cancelled", Order(factory, "ORD-1003"));
    }

    [Fact]
    public async Task A_turn_that_fails_after_the_stream_started_ends_with_a_failed_final_event()
    {
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        var (status, _, events, _) = await SseClient.PostAsync(api.Http, $"/api/v1/conversations/{conversation}/turns", new { client_turn_id = Guid.NewGuid().ToString(), message = $"Where is ORD-1002? {FakeChatClient.FailureTrigger}" });

        Assert.Equal(HttpStatusCode.OK, status);
        var final = SseClient.Final(events);
        Assert.Equal("failed", final.GetProperty("outcome").GetString());
        Assert.Equal("internal_error", final.GetProperty("error").GetProperty("code").GetString());
        Assert.False(final.TryGetProperty("reply", out _));
        Assert.Equal(string.Empty, SseClient.CountedText(events));
    }

    [Fact]
    public async Task Errors_found_before_the_turn_starts_are_ordinary_problem_responses()
    {
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var clientTurnId = Guid.NewGuid().ToString();
        await api.AskAsync(conversation, "Where is my order ORD-1002?", clientTurnId);

        var (missing, missingType, _, notFound) = await SseClient.PostAsync(api.Http, "/api/v1/conversations/conv_unknown/turns", new { client_turn_id = Guid.NewGuid().ToString(), message = "Hi" });
        var (reused, _, _, conflict) = await SseClient.PostAsync(api.Http, $"/api/v1/conversations/{conversation}/turns", new { client_turn_id = clientTurnId, message = "Something else" });

        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Equal("application/problem+json", missingType);
        Assert.Equal("not_found", notFound!.Value.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, reused);
        Assert.Equal("client_turn_id_reused", conflict!.Value.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_duplicate_streamed_submission_reports_the_one_run_without_running_again()
    {
        var model = new CountingScript();
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001", chatClient: model.Client());
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var body = new { client_turn_id = Guid.NewGuid().ToString(), message = "Where is my order ORD-1002?" };
        var first = SseClient.Final((await SseClient.PostAsync(api.Http, $"/api/v1/conversations/{conversation}/turns", body)).Events);
        var calls = model.Calls;

        var (_, _, events, _) = await SseClient.PostAsync(api.Http, $"/api/v1/conversations/{conversation}/turns", body);

        var again = SseClient.Final(events);
        Assert.Equal(first.GetProperty("turn_id").GetString(), again.GetProperty("turn_id").GetString());
        Assert.Equal(calls, model.Calls);
        Assert.Equal(again.ReplyText(), SseClient.CountedText(events));
    }

    [Fact]
    public async Task Long_text_is_split_into_deltas_within_the_contract_limit()
    {
        var longAnswer = new string('x', 4500);
        var model = new CountingScript(messages => new ChatResponse(new ChatMessage(ChatRole.Assistant, longAnswer)));
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001", chatClient: model.Client());
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        // Every event is schema-checked as it is parsed, so no delta may exceed 2000 characters.
        var (_, _, events, _) = await SseClient.PostAsync(api.Http, $"/api/v1/conversations/{conversation}/turns", new { client_turn_id = Guid.NewGuid().ToString(), message = "Where is my order ORD-1002?" });

        Assert.Equal(3, events.Count(e => e.GetProperty("type").GetString() == "reply.delta"));
        Assert.Equal(longAnswer, SseClient.CountedText(events));
        Assert.Equal(longAnswer, SseClient.Final(events).ReplyText());
    }

    private static string Order(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string orderId) =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Support.Api.Modules.Orders.Application.IOrderRepository>(factory.Services).Find(orderId)!.Status;
}
