using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Agents.Infrastructure.ModelProvider;

namespace Support.Api.Tests.Modules.Conversations;

/// <summary>
/// A conversation remembers its own context. Follow-ups work without repeating the id, only verified facts
/// are remembered, conversations never share state, history is bounded, and one turn runs at a time.
/// </summary>
public sealed class SessionTests
{
    [Fact]
    public async Task An_order_follow_up_works_without_repeating_the_id()
    {
        await using var factory = Start();
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        await api.AskAsync(conversation, "Where is my order ORD-1002?");
        var followUp = await api.AskAsync(conversation, "When was it shipped?");

        Assert.Equal("orders", followUp.GetProperty("route").GetProperty("target").GetString());
        Assert.Equal("answer", followUp.ReplyKind());
        Assert.Contains("ORD-1002", followUp.ReplyText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_follow_up_to_another_specialist_uses_the_verified_order()
    {
        await using var factory = Start();
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        await api.AskAsync(conversation, "Where is my order ORD-1002?");
        var invoice = await api.AskAsync(conversation, "Can I get the invoice for it?");

        Assert.Equal("billing", invoice.GetProperty("route").GetProperty("target").GetString());
        Assert.Equal("answer", invoice.ReplyKind());
        Assert.Contains("INV-5002", invoice.ReplyText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("What's the status of ORD-2001?")] // another customer's order
    [InlineData("What's the status of ORD-9999?")] // no such order
    public async Task An_order_that_was_not_verified_is_never_remembered(string firstMessage)
    {
        await using var factory = Start();
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        await api.AskAsync(conversation, firstMessage);
        var followUp = await api.AskAsync(conversation, "When was it shipped?");

        // Not "ORD-2001 shipped …": the follow-up asks for an id, because nothing was verified for this customer.
        Assert.Equal("clarification", followUp.ReplyKind());
        Assert.DoesNotContain("ORD-2001", followUp.ReplyText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conversations_and_customers_never_share_state()
    {
        var principals = new SwitchablePrincipalSource("CUST-1001");
        await using var factory = SupportApiFactory.Create(principalSource: principals);
        var api = new ApiClient(factory.CreateClient());

        var first = await api.CreateConversationAsync();
        await api.AskAsync(first, "Where is my order ORD-1002?");

        // Same customer, new conversation: no context carries over.
        var second = await api.CreateConversationAsync();
        Assert.Equal("clarification", (await api.AskAsync(second, "When was it shipped?")).ReplyKind());

        // Another customer cannot reach the first conversation at all.
        principals.SwitchTo("CUST-1002");
        var (status, problem) = await api.SendTurnAsync(first, new { client_turn_id = Guid.NewGuid().ToString(), message = "When was it shipped?" });
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_specialist_keeps_its_native_session_with_bounded_history()
    {
        var seen = new ConcurrentQueue<IReadOnlyList<ChatMessage>>();
        await using var factory = Start(new CountingScript(messages =>
        {
            seen.Enqueue([.. messages]);
            return FakeChatClient.DefaultScript(messages);
        }));
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        await api.AskAsync(conversation, "Where is my order ORD-1001?");
        for (var turn = 0; turn < 10; turn++)
        {
            await api.AskAsync(conversation, $"Where is my order ORD-100{(turn % 3) + 1}?");
        }

        await api.AskAsync(conversation, "When was it shipped?");
        await api.AskAsync(conversation, "When will it be delivered?");
        var lastCall = seen.Last();
        var conversationMessages = lastCall.Where(message => message.Role != ChatRole.System && !message.Contents.Any(c => c is FunctionCallContent or FunctionResultContent)).ToList();

        // The session came back: earlier turns are in the input. And it is bounded: at most the kept history plus this message.
        Assert.Contains(conversationMessages, message => message.Text.Contains("ORD-100", StringComparison.Ordinal) && message.Role == ChatRole.User);
        Assert.InRange(conversationMessages.Count, 2, ConversationHistory.MaxMessages + 1);
        // Two follow-ups in a row each got a context note, but notes are for one turn only: just the current one remains.
        Assert.Equal(1, lastCall.Count(ConversationHistory.IsContextNote));
    }

    [Fact]
    public async Task Each_earlier_message_reaches_the_specialist_exactly_once()
    {
        // Guards against re-saving loaded history on every turn (a real bug: a custom storage filter had
        // replaced MAF's default, which skips messages that came from history).
        var seen = new ConcurrentQueue<IReadOnlyList<ChatMessage>>();
        await using var factory = Start(new CountingScript(messages =>
        {
            seen.Enqueue([.. messages]);
            return FakeChatClient.DefaultScript(messages);
        }));
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        string[] questions = ["Where is my order ORD-1001?", "Where is my order ORD-1002?", "Where is my order ORD-1003?", "When will it be delivered?"];

        foreach (var question in questions)
        {
            await api.AskAsync(conversation, question);
        }

        var userTexts = seen.Last().Where(message => message.Role == ChatRole.User).Select(message => message.Text).ToList();
        Assert.Equal(questions, userTexts);
    }

    [Fact]
    public async Task One_turn_at_a_time_per_conversation_while_others_proceed()
    {
        var gate = new GatedModel();
        await using var factory = SupportApiFactory.Create(chatClient: gate.Client);
        var api = new ApiClient(factory.CreateClient());
        var busy = await api.CreateConversationAsync();
        var other = await api.CreateConversationAsync();

        var running = api.SendTurnAsync(busy, new { client_turn_id = Guid.NewGuid().ToString(), message = "Where is my order ORD-1002?" });
        await gate.FirstCallStarted.WaitAsync(TestContext.Current.CancellationToken);

        var (state, conversation) = await api.SendAsync(HttpMethod.Get, $"/api/v1/conversations/{busy}");
        var (status, problem) = await api.SendTurnAsync(busy, new { client_turn_id = Guid.NewGuid().ToString(), message = "And ORD-1003?" });
        var elsewhere = await api.AskAsync(other, "Where is my order ORD-1003?");
        gate.Release();
        var (finished, firstTurn) = await running;

        Assert.Equal(HttpStatusCode.OK, state);
        Assert.Equal(JsonValueKind.String, conversation.GetProperty("active_turn_id").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("turn_in_progress", problem.GetProperty("code").GetString());
        Assert.Equal("answer", elsewhere.ReplyKind());
        Assert.Equal(HttpStatusCode.OK, finished);
        Assert.Equal(conversation.GetProperty("active_turn_id").GetString(), firstTurn.GetProperty("turn_id").GetString());

        var (_, after) = await api.SendAsync(HttpMethod.Get, $"/api/v1/conversations/{busy}");
        Assert.Equal(JsonValueKind.Null, after.GetProperty("active_turn_id").ValueKind);
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Start(CountingScript? model = null) =>
        SupportApiFactory.Create(principal: "CUST-1001", chatClient: (model ?? new CountingScript()).Client());
}
