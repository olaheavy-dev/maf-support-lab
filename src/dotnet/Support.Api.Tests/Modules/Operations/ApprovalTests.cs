using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Support.Api.Modules.Agents.Infrastructure.ModelProvider;
using Support.Api.Modules.Billing.Application;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;
using Support.Api.Modules.Operations.Presentation;
using Support.Api.Modules.Orders.Application;
using Support.Api.Modules.Technical.Application;

namespace Support.Api.Tests.Modules.Operations;

/// <summary>
/// L10: every simulated write waits for the customer's approval of the exact action, bound to the customer, the
/// conversation, the operation, and its arguments. No approval or a rejection changes nothing; an approved eligible
/// write changes simulated state once and reports the facts.
/// </summary>
public sealed class ApprovalTests
{
    public static TheoryData<string, string, string, string> Writes() => new()
    {
        { "Please cancel order ORD-1003.", "orders", "cancel_order", """{"order_id":"ORD-1003"}""" },
        { "I want a refund for ORD-1001, the headphones crackle.", "billing", "refund_order", """{"order_id":"ORD-1001","reason":"I want a refund for ORD-1001, the headphones crackle."}""" },
        { "Please open a support ticket: my fridge shows error Z-77.", "technical", "create_ticket", """{"description":"Please open a support ticket: my fridge shows error Z-77.","priority":"normal","subject":"Support request"}""" },
    };

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_write_waits_for_approval_of_the_exact_action_and_runs_once_approved(string message, string target, string kind, string arguments)
    {
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        var asked = await api.AskAsync(conversation, message);

        Assert.Equal("awaiting_approval", asked.GetProperty("outcome").GetString());
        Assert.Equal(target, asked.GetProperty("route").GetProperty("target").GetString());
        Assert.Equal("approval_request", asked.ReplyKind());
        var pending = asked.GetProperty("pending_approval");
        Assert.Equal(kind, pending.GetProperty("kind").GetString());
        Assert.Equal(Canonical(arguments), Canonical(pending.GetProperty("arguments").GetRawText()));
        var operationId = pending.GetProperty("operation_id").GetString()!;
        Assert.Equal("awaiting_approval", (await api.GetOperationAsync(operationId)).GetProperty("status").GetString());
        Assert.Equal(StateBefore(kind), State(factory, kind)); // nothing changed yet

        var (status, decided) = await api.DecideAsync(conversation, pending.GetProperty("approval_id").GetString()!, operationId, "approve");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("approval_result", decided.ReplyKind());
        Assert.Equal(asked.GetProperty("turn_id").GetString(), decided.GetProperty("resumed_from_turn_id").GetString());
        Assert.Equal(JsonValueKind.Null, decided.GetProperty("client_turn_id").ValueKind);
        var operation = await api.GetOperationAsync(operationId);
        Assert.Equal("committed", operation.GetProperty("status").GetString());
        Assert.Equal(StateAfter(kind), State(factory, kind));
        Assert.Empty((await api.GetConversationAsync(conversation)).GetProperty("pending_approvals").EnumerateArray());
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_rejected_write_changes_nothing_and_is_not_asked_again(string message, string target, string kind, string arguments)
    {
        _ = target;
        _ = arguments;
        // A stubborn model: it requests the same write every time, even after the rejection (the native rejection tells
        // it not to retry).
        var model = new CountingScript(RequestTheWrite);
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001", chatClient: model.Client());
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        var asked = await api.AskAsync(conversation, message);
        var pending = asked.GetProperty("pending_approval");
        var operationId = pending.GetProperty("operation_id").GetString()!;
        var (status, decided) = await api.DecideAsync(conversation, pending.GetProperty("approval_id").GetString()!, operationId, "reject");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("approval_result", decided.ReplyKind());
        Assert.Contains("Nothing has been changed", decided.ReplyText(), StringComparison.Ordinal);
        Assert.Equal("rejected", (await api.GetOperationAsync(operationId)).GetProperty("status").GetString());
        Assert.Equal(StateBefore(kind), State(factory, kind));
        // Every repeated request was declined automatically and the loop is bounded: 1 call to ask, 1 to resume after the
        // rejection, then at most 3 declines. No second operation exists or is pending, and the customer is not asked again.
        Assert.Equal(5, model.Calls);
        Assert.Single(factory.Services.GetRequiredService<IOperationStore>().ForConversation(conversation));
        Assert.Empty((await api.GetConversationAsync(conversation)).GetProperty("pending_approvals").EnumerateArray());

        // The write the customer originally asked for, with the same arguments as the first time.
        static ChatResponse RequestTheWrite(IReadOnlyList<ChatMessage> messages) =>
            FakeChatClient.DefaultScript([.. messages.Where(m => m.Role == ChatRole.User && m.Contents.OfType<TextContent>().Any()).Take(1)], new ChatOptions { Tools = [.. ToolsFor(messages)] });
    }

    [Theory]
    [InlineData("CUST-1001", "Cancel order ORD-1001.", "not_eligible")] // already delivered
    [InlineData("CUST-1002", "Refund my keyboard order ORD-2001 please.", "not_eligible")] // outside the 30-day window
    [InlineData("CUST-1001", "Refund ORD-2003 to my card right away.", "not_found")] // another customer's order
    [InlineData("CUST-1001", "Please cancel order ORD-9999.", "not_found")]
    public async Task An_ineligible_or_foreign_write_is_never_proposed(string principal, string message, string kind)
    {
        await using var factory = SupportApiFactory.Create(principal: principal);
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        var turn = await api.AskAsync(conversation, message);

        Assert.Equal("completed", turn.GetProperty("outcome").GetString());
        Assert.Equal(kind, turn.ReplyKind());
        Assert.Empty(turn.GetProperty("operations").EnumerateArray());
        Assert.Empty(factory.Services.GetRequiredService<IOperationStore>().ForConversation(conversation));
    }

    [Fact]
    public async Task An_approval_is_bound_to_its_customer_conversation_operation_and_decision()
    {
        var principals = new SwitchablePrincipalSource("CUST-1001");
        await using var factory = SupportApiFactory.Create(principalSource: principals);
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var other = await api.CreateConversationAsync();
        var asked = await api.AskAsync(conversation, "Please cancel order ORD-1003.");
        var pending = asked.GetProperty("pending_approval");
        var approvalId = pending.GetProperty("approval_id").GetString()!;
        var operationId = pending.GetProperty("operation_id").GetString()!;
        var unrelated = await api.AskAsync(other, "Please cancel order ORD-1003.");
        var otherOperationId = unrelated.GetProperty("pending_approval").GetProperty("operation_id").GetString()!;

        // Another customer, and the same customer through another conversation, cannot reach it.
        principals.SwitchTo("CUST-1002");
        Assert.Equal(HttpStatusCode.NotFound, (await api.DecideAsync(conversation, approvalId, operationId, "approve")).Status);
        principals.SwitchTo("CUST-1001");
        Assert.Equal(HttpStatusCode.NotFound, (await api.DecideAsync(other, approvalId, operationId, "approve")).Status);

        // A different operation id is a mismatch, and arguments cannot be sent at all.
        var (mismatch, mismatchProblem) = await api.DecideAsync(conversation, approvalId, otherOperationId, "approve");
        Assert.Equal(HttpStatusCode.Conflict, mismatch);
        Assert.Equal("approval_mismatch", mismatchProblem.GetProperty("code").GetString());
        var (withArguments, _) = await api.DecideAsync(conversation, approvalId, operationId, "approve",
            new { operation_id = operationId, decision = "approve", arguments = new { order_id = "ORD-1002" } });
        Assert.Equal(HttpStatusCode.BadRequest, withArguments);
        Assert.Equal("processing", Order(factory, "ORD-1003")); // none of the above changed anything

        // The decision is recorded once: repeating it replays the same turn; a different one is refused.
        var (approved, first) = await api.DecideAsync(conversation, approvalId, operationId, "approve");
        var (again, second) = await api.DecideAsync(conversation, approvalId, operationId, "approve");
        var (changed, changedProblem) = await api.DecideAsync(conversation, approvalId, operationId, "reject");
        Assert.Equal(HttpStatusCode.OK, approved);
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.Equal(first.GetProperty("turn_id").GetString(), second.GetProperty("turn_id").GetString());
        Assert.Equal(HttpStatusCode.Conflict, changed);
        Assert.Equal("approval_already_decided", changedProblem.GetProperty("code").GetString());
        Assert.Equal("cancelled", Order(factory, "ORD-1003"));
    }

    [Fact]
    public async Task An_expired_approval_cannot_be_used()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001", configureServices: services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var pending = (await api.AskAsync(conversation, "Please cancel order ORD-1003.")).GetProperty("pending_approval");
        var operationId = pending.GetProperty("operation_id").GetString()!;

        clock.Advance(TimeSpan.FromMinutes(16));
        var (status, problem) = await api.DecideAsync(conversation, pending.GetProperty("approval_id").GetString()!, operationId, "approve");

        Assert.Equal(HttpStatusCode.Gone, status);
        Assert.Equal("approval_expired", problem.GetProperty("code").GetString());
        Assert.Equal("expired", (await api.GetOperationAsync(operationId)).GetProperty("status").GetString());
        Assert.Equal("processing", Order(factory, "ORD-1003"));
    }

    [Fact]
    public async Task Eligibility_is_checked_again_when_the_approved_write_runs()
    {
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        var api = new ApiClient(factory.CreateClient());
        var first = await api.CreateConversationAsync();
        var second = await api.CreateConversationAsync();
        var a = (await api.AskAsync(first, "Please cancel order ORD-1003.")).GetProperty("pending_approval");
        var b = (await api.AskAsync(second, "Please cancel order ORD-1003.")).GetProperty("pending_approval");

        await api.DecideAsync(first, a.GetProperty("approval_id").GetString()!, a.GetProperty("operation_id").GetString()!, "approve");
        var (status, late) = await api.DecideAsync(second, b.GetProperty("approval_id").GetString()!, b.GetProperty("operation_id").GetString()!, "approve");

        // Approved, but no longer eligible: the second one fails factually and changes nothing more.
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Nothing has been changed", late.ReplyText(), StringComparison.Ordinal);
        var operation = await api.GetOperationAsync(b.GetProperty("operation_id").GetString()!);
        Assert.Equal("failed", operation.GetProperty("status").GetString());
        Assert.Equal("not_eligible", operation.GetProperty("failure").GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_write_tool_cannot_run_without_an_approval_for_exactly_its_arguments()
    {
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var pending = (await api.AskAsync(conversation, "Please cancel order ORD-1003.")).GetProperty("pending_approval");
        var operation = factory.Services.GetRequiredService<IOperationStore>().Find(pending.GetProperty("operation_id").GetString()!)!;
        var tools = factory.Services.GetRequiredService<OperationTools>();
        var cancellationToken = TestContext.Current.CancellationToken;

        // Called directly (as if the native gate were bypassed): no approved operation in scope, nothing runs.
        Assert.Equal("not_approved", (await tools.CancelOrderAsync("ORD-1003", cancellationToken)).Status);

        // Even with the operation approved and in scope, changed arguments cannot reuse it.
        operation.TryDecide(ApprovalDecision.Approve, DateTimeOffset.UtcNow);
        using (ApprovalScope.Begin(operation))
        {
            Assert.Equal("not_approved", (await tools.CancelOrderAsync("ORD-1002", cancellationToken)).Status);
            Assert.Equal("not_approved", (await tools.RefundOrderAsync("ORD-1003", "x", cancellationToken)).Status);
        }

        Assert.Equal("processing", Order(factory, "ORD-1003"));
        Assert.Equal("shipped", Order(factory, "ORD-1002"));
    }

    [Theory]
    [InlineData("approve", "committed", "cancelled")]
    [InlineData("reject", "rejected", "processing")]
    public async Task A_decision_stays_replayable_when_the_resumed_model_call_fails(string decision, string status, string orderStatus)
    {
        // The model fails on any call that follows a tool result: after the approved write commits, or after the
        // rejection reaches it. The decision is recorded either way, so the turn must still finish and replay.
        var model = new CountingScript(messages => messages[^1].Contents.OfType<FunctionResultContent>().Any()
            ? throw new InvalidOperationException("Simulated provider failure after the decision.")
            : FakeChatClient.DefaultScript(messages, new ChatOptions { Tools = [AIFunctionFactory.Create(() => 0, FakeChatClient.CancelOrderToolName)] }));
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001", chatClient: model.Client());
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var pending = (await api.AskAsync(conversation, "Please cancel order ORD-1003.")).GetProperty("pending_approval");
        var approvalId = pending.GetProperty("approval_id").GetString()!;
        var operationId = pending.GetProperty("operation_id").GetString()!;

        var (first, decided) = await api.DecideAsync(conversation, approvalId, operationId, decision);
        var (again, replayed) = await api.DecideAsync(conversation, approvalId, operationId, decision);

        Assert.Equal(HttpStatusCode.OK, first);
        Assert.Equal("approval_result", decided.ReplyKind());
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.Equal(decided.GetProperty("turn_id").GetString(), replayed.GetProperty("turn_id").GetString());
        Assert.Equal(status, (await api.GetOperationAsync(operationId)).GetProperty("status").GetString());
        Assert.Equal(orderStatus, Order(factory, "ORD-1003"));
    }

    [Fact]
    public async Task A_refund_with_the_longest_allowed_reason_still_has_a_contract_valid_summary()
    {
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var message = "I want a refund for ORD-1001. " + new string('x', 500 - "I want a refund for ORD-1001. ".Length);

        // AskAsync and GetConversationAsync validate against the contract (approval.summary is at most 500 characters).
        var pending = (await api.AskAsync(conversation, message)).GetProperty("pending_approval");
        await api.GetConversationAsync(conversation);

        Assert.Equal(500, pending.GetProperty("arguments").GetProperty("reason").GetString()!.Length); // the full reason is what's approved
        Assert.True(pending.GetProperty("summary").GetString()!.Length <= 500);
    }

    private static string Canonical(string json) =>
        JsonSerializer.Serialize(JsonSerializer.Deserialize<SortedDictionary<string, string>>(json));

    private static IEnumerable<AITool> ToolsFor(IReadOnlyList<ChatMessage> messages)
    {
        var text = messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        string name = text.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? FakeChatClient.CancelOrderToolName
            : text.Contains("refund", StringComparison.OrdinalIgnoreCase) ? FakeChatClient.RefundOrderToolName
            : FakeChatClient.CreateTicketToolName;
        yield return AIFunctionFactory.Create(() => 0, name);
    }

    private static string Order(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string orderId) =>
        factory.Services.GetRequiredService<IOrderRepository>().Find(orderId)!.Status;

    private static string StateBefore(string kind) => kind switch
    {
        "cancel_order" => "processing",
        "refund_order" => "refunds=0",
        _ => "tickets=0",
    };

    private static string StateAfter(string kind) => kind switch
    {
        "cancel_order" => "cancelled",
        "refund_order" => "refunds=1",
        _ => "tickets=1",
    };

    private static string State(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string kind) => kind switch
    {
        "cancel_order" => Order(factory, "ORD-1003"),
        "refund_order" => $"refunds={factory.Services.GetRequiredService<IBillingRepository>().PaymentsFor("CUST-1001").Count(payment => payment.Kind == "refund")}",
        _ => $"tickets={factory.Services.GetRequiredService<ITicketStore>().OwnedBy("CUST-1001").Count}",
    };
}
