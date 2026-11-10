using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Support.Api.Common.Infrastructure.Persistence;
using Support.Api.Modules.Agents.Infrastructure.ModelProvider;
using Support.Api.Modules.Billing.Application;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;
using Support.Api.Modules.Orders.Application;
using Support.Api.Modules.Technical.Application;

namespace Support.Api.Tests.Modules.Persistence;

/// <summary>
/// L11: state survives a restart, and every write applies at most once. A "restart" is a new host started on the same
/// SQLite file after the first host has stopped (or, for the crash cases, while the first host is stuck mid-turn).
/// </summary>
public sealed class DurableStateTests
{
    public static TheoryData<string, string> Writes() => new()
    {
        { "Please cancel order ORD-1003.", "cancel_order" },
        { "I want a refund for ORD-1001, the headphones crackle.", "refund_order" },
        { "Please open a support ticket: my fridge shows error Z-77.", "create_ticket" },
    };

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_pending_approval_survives_a_restart_and_runs_once_when_approved(string message, string kind)
    {
        using var database = new TestDatabase();
        string conversation, approvalId, operationId, askedTurnId;
        await using (var before = Host(database))
        {
            var api = new ApiClient(before.CreateClient());
            conversation = await api.CreateConversationAsync();
            var asked = await api.AskAsync(conversation, message);
            var pending = asked.GetProperty("pending_approval");
            (approvalId, operationId, askedTurnId) = (pending.GetProperty("approval_id").GetString()!, pending.GetProperty("operation_id").GetString()!, asked.GetProperty("turn_id").GetString()!);
        }

        string decidedTurnId;
        await using (var after = Host(database))
        {
            var api = new ApiClient(after.CreateClient());
            var restored = await api.GetConversationAsync(conversation);
            Assert.Equal(operationId, Assert.Single(restored.GetProperty("pending_approvals").EnumerateArray()).GetProperty("operation_id").GetString());
            Assert.Equal(StateBefore(kind), State(after, kind));

            var (status, decided) = await api.DecideAsync(conversation, approvalId, operationId, "approve");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("approval_result", decided.ReplyKind());
            Assert.Equal(askedTurnId, decided.GetProperty("resumed_from_turn_id").GetString());
            Assert.Equal("committed", (await api.GetOperationAsync(operationId)).GetProperty("status").GetString());
            Assert.Equal(StateAfter(kind), State(after, kind));
            decidedTurnId = decided.GetProperty("turn_id").GetString()!;
        }

        // Restarted again: repeating the decision replays the stored turn and never writes again.
        await using (var later = Host(database))
        {
            var api = new ApiClient(later.CreateClient());
            var (status, replayed) = await api.DecideAsync(conversation, approvalId, operationId, "approve");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(decidedTurnId, replayed.GetProperty("turn_id").GetString());
            Assert.Equal(StateAfter(kind), State(later, kind));
        }
    }

    [Fact]
    public async Task After_a_restart_a_wrong_owner_mismatch_or_rejection_still_changes_nothing()
    {
        using var database = new TestDatabase();
        var principals = new SwitchablePrincipalSource("CUST-1001");
        var (conversation, approvalId, operationId) = await AskInOneHostAsync(database, "Please cancel order ORD-1003.");

        await using var after = SupportApiFactory.Create(settings: database.Settings, principalSource: principals);
        var api = new ApiClient(after.CreateClient());

        principals.SwitchTo("CUST-1002");
        Assert.Equal(HttpStatusCode.NotFound, (await api.DecideAsync(conversation, approvalId, operationId, "approve")).Status);
        principals.SwitchTo("CUST-1001");
        var (mismatch, problem) = await api.DecideAsync(conversation, approvalId, "op_0000000000000000", "approve");
        Assert.Equal(HttpStatusCode.Conflict, mismatch);
        Assert.Equal("approval_mismatch", problem.GetProperty("code").GetString());
        Assert.Equal("processing", Order(after, "ORD-1003"));

        var (status, rejected) = await api.DecideAsync(conversation, approvalId, operationId, "reject");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Nothing has been changed", rejected.ReplyText(), StringComparison.Ordinal);
        var (approveLater, conflict) = await api.DecideAsync(conversation, approvalId, operationId, "approve");
        Assert.Equal(HttpStatusCode.Conflict, approveLater);
        Assert.Equal("approval_already_decided", conflict.GetProperty("code").GetString());
        Assert.Equal("rejected", (await api.GetOperationAsync(operationId)).GetProperty("status").GetString());
        Assert.Equal("processing", Order(after, "ORD-1003"));
    }

    [Fact]
    public async Task An_approval_that_expired_while_the_service_was_down_cannot_run()
    {
        using var database = new TestDatabase();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var (conversation, approvalId, operationId) = await AskInOneHostAsync(database, "Please cancel order ORD-1003.", clock);

        clock.Advance(TimeSpan.FromMinutes(16));
        await using var after = Host(database, clock);
        var api = new ApiClient(after.CreateClient());
        var (status, problem) = await api.DecideAsync(conversation, approvalId, operationId, "approve");

        Assert.Equal(HttpStatusCode.Gone, status);
        Assert.Equal("approval_expired", problem.GetProperty("code").GetString());
        Assert.Equal("expired", (await api.GetOperationAsync(operationId)).GetProperty("status").GetString());
        Assert.Equal("processing", Order(after, "ORD-1003"));
    }

    [Fact]
    public async Task Duplicate_concurrent_approvals_produce_one_mutation()
    {
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var pending = (await api.AskAsync(conversation, "Please open a support ticket: my fridge shows error Z-77.")).GetProperty("pending_approval");

        var decisions = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            api.DecideAsync(conversation, pending.GetProperty("approval_id").GetString()!, pending.GetProperty("operation_id").GetString()!, "approve")));

        Assert.All(decisions, decision => Assert.Equal(HttpStatusCode.OK, decision.Status));
        Assert.Single(decisions.Select(decision => decision.Body.GetProperty("turn_id").GetString()).Distinct());
        Assert.Equal("tickets=1", State(factory, "create_ticket"));
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_write_committed_just_before_a_crash_is_never_applied_again(string message, string kind)
    {
        // The first host commits the write, then hangs in the model call that follows the tool result, so the decision
        // turn is never stored: as if the process died right after the commit. A new host on the same file then gets
        // the same decision again (a client retry).
        using var database = new TestDatabase();
        var (conversation, approvalId, operationId) = await AskInOneHostAsync(database, message);
        using var hang = new ManualResetEventSlim();
        var stuck = new CountingScript(messages =>
        {
            if (messages[^1].Contents.OfType<FunctionResultContent>().Any())
            {
                hang.Wait(TimeSpan.FromSeconds(30));
                throw new InvalidOperationException("The host was stopped.");
            }

            return FakeChatClient.DefaultScript(messages, new ChatOptions { Tools = [.. ToolsFor(kind)] });
        });
        await using var crashed = Host(database, chatClient: stuck.Client());
        var crashedApi = new ApiClient(crashed.CreateClient());
        var abandoned = crashedApi.DecideAsync(conversation, approvalId, operationId, "approve");
        await WaitForAsync(() => crashed.Services.GetRequiredService<IOperationStore>().Find(operationId)!.Status == OperationStatus.Committed);

        string retriedTurnId;
        await using (var restarted = Host(database))
        {
            var api = new ApiClient(restarted.CreateClient());
            var (status, retried) = await api.DecideAsync(conversation, approvalId, operationId, "approve");
            retriedTurnId = retried.GetProperty("turn_id").GetString()!;

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("approval_result", retried.ReplyKind());
            Assert.StartsWith("Done.", retried.ReplyText(), StringComparison.Ordinal); // from the stored record; no model call
            Assert.Equal("committed", (await api.GetOperationAsync(operationId)).GetProperty("status").GetString());
            Assert.Equal(StateAfter(kind), State(restarted, kind));
        }

        // The stuck host finally finishes: its decision turn loses to the one already stored, so it answers with that
        // stored turn, and still nothing was written twice.
        hang.Set();
        var (lateStatus, late) = await abandoned;
        Assert.Equal(HttpStatusCode.OK, lateStatus);
        Assert.Equal(retriedTurnId, late.GetProperty("turn_id").GetString());
        Assert.Equal(StateAfter(kind), State(crashed, kind));
    }

    [Fact]
    public async Task An_approved_write_that_never_ran_before_a_crash_is_resumed_once()
    {
        // The decision was stored, then the process stopped before the resumed run wrote anything. Nothing was applied
        // (the write and its commit are one transaction), so the retried decision resumes the run, and it commits once.
        using var database = new TestDatabase();
        var (conversation, approvalId, operationId) = await AskInOneHostAsync(database, "Please open a support ticket: my fridge shows error Z-77.");
        await using (var crashed = Host(database))
        {
            var store = crashed.Services.GetRequiredService<IOperationStore>();
            var operation = store.Find(operationId)!;
            Assert.True(operation.TryDecide(ApprovalDecision.Approve, DateTimeOffset.UtcNow) && store.TryUpdate(operation, OperationStatus.AwaitingApproval));
        }

        await using var restarted = Host(database);
        var api = new ApiClient(restarted.CreateClient());
        var (status, decided) = await api.DecideAsync(conversation, approvalId, operationId, "approve");
        var (again, replayed) = await api.DecideAsync(conversation, approvalId, operationId, "approve");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("committed", (await api.GetOperationAsync(operationId)).GetProperty("status").GetString());
        Assert.Equal("tickets=1", State(restarted, "create_ticket"));
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.Equal(decided.GetProperty("turn_id").GetString(), replayed.GetProperty("turn_id").GetString());
    }

    [Fact]
    public async Task A_stale_run_of_a_finished_operation_rolls_its_write_back()
    {
        // Another run (here: a copy of the operation still believing it is "running") reaches the write after the
        // operation was committed. Its ticket insert and its commit are one transaction, and the commit is guarded on
        // the stored status, so the whole write rolls back and the stored result is reported.
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var pending = (await api.AskAsync(conversation, "Please open a support ticket: my fridge shows error Z-77.")).GetProperty("pending_approval");
        var operationId = pending.GetProperty("operation_id").GetString()!;
        await api.DecideAsync(conversation, pending.GetProperty("approval_id").GetString()!, operationId, "approve");
        var committed = factory.Services.GetRequiredService<IOperationStore>().Find(operationId)!;
        var stale = Operation.FromSnapshot(committed.ToSnapshot() with { Status = OperationStatus.Running, ResultJson = null });

        Common.Domain.Result<System.Text.Json.Nodes.JsonObject> result;
        using (ApprovalScope.Begin(stale))
        {
            result = await factory.Services.GetRequiredService<OperationService>().ExecuteApprovedAsync(OperationKind.CreateTicket, stale.Arguments, TestContext.Current.CancellationToken);
        }

        Assert.True(result.IsSuccess);
        Assert.Equal(committed.Result!.ToJsonString(), result.Value.ToJsonString());
        Assert.Equal(OperationStatus.Committed, stale.Status);
        Assert.Equal("tickets=1", State(factory, "create_ticket"));
    }

    [Fact]
    public async Task A_conversation_and_its_specialist_history_survive_a_restart()
    {
        using var database = new TestDatabase();
        string conversation, firstTurnId;
        var firstKey = Guid.NewGuid().ToString();
        await using (var before = Host(database))
        {
            var api = new ApiClient(before.CreateClient());
            conversation = await api.CreateConversationAsync();
            firstTurnId = (await api.AskAsync(conversation, "Where is my order ORD-1002?", clientTurnId: firstKey)).GetProperty("turn_id").GetString()!;
        }

        var seen = new ConcurrentQueue<IReadOnlyList<ChatMessage>>();
        var model = new CountingScript(messages =>
        {
            seen.Enqueue([.. messages]);
            return FakeChatClient.DefaultScript(messages, new ChatOptions { Tools = [AIFunctionFactory.Create(() => 0, FakeChatClient.OrderStatusToolName)] });
        });
        await using var after = Host(database, chatClient: model.Client());
        var restartedApi = new ApiClient(after.CreateClient());

        var followUp = await restartedApi.AskAsync(conversation, "When was it shipped?");
        var callsBeforeReplay = model.Calls;
        var (replayStatus, replayed) = await restartedApi.SendTurnAsync(conversation, new { client_turn_id = firstKey, message = "Where is my order ORD-1002?" });

        // The verified order came back (no "which order?"), and the specialist saw the earlier exchange from its stored session.
        Assert.Equal("answer", followUp.ReplyKind());
        Assert.Contains("ORD-1002", followUp.ReplyText(), StringComparison.Ordinal);
        Assert.Contains(seen.First(), message => message.Role == ChatRole.User && message.Text == "Where is my order ORD-1002?");
        // The first turn replays from storage: same turn, no new model call.
        Assert.Equal(HttpStatusCode.OK, replayStatus);
        Assert.Equal(firstTurnId, replayed.GetProperty("turn_id").GetString());
        Assert.Equal(callsBeforeReplay, model.Calls);
    }

    [Fact]
    public async Task An_approval_is_never_listed_without_its_paused_request()
    {
        // Codex review on PR #77: the operation and its native request are one transaction. If storing the request
        // fails (as if the process stopped in between), no operation is left behind as an approval nobody can resume.
        using var database = new TestDatabase();
        string conversation;
        await using (var host = Host(database))
        {
            host.Services.GetRequiredService<SupportDatabase>().Execute(
                "CREATE TRIGGER stop_here BEFORE INSERT ON native_approval_requests BEGIN SELECT RAISE(ABORT, 'simulated stop'); END;");
            var api = new ApiClient(host.CreateClient());
            conversation = await api.CreateConversationAsync();
            var status = await PostTurnStatusAsync(host, conversation, Guid.NewGuid().ToString(), "Please cancel order ORD-1003.");
            Assert.Equal(HttpStatusCode.InternalServerError, status);
            host.Services.GetRequiredService<SupportDatabase>().Execute("DROP TRIGGER stop_here");
        }

        await using var restarted = Host(database);
        var restartedApi = new ApiClient(restarted.CreateClient());
        Assert.Empty((await restartedApi.GetConversationAsync(conversation)).GetProperty("pending_approvals").EnumerateArray());
        Assert.Empty(restarted.Services.GetRequiredService<IOperationStore>().ForConversation(conversation));
    }

    [Fact]
    public async Task An_approved_operation_whose_paused_request_is_missing_fails_instead_of_staying_running()
    {
        // Defence in depth: if the paused request is gone anyway, approving records a factual failure and changes nothing.
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var pending = (await api.AskAsync(conversation, "Please cancel order ORD-1003.")).GetProperty("pending_approval");
        var operationId = pending.GetProperty("operation_id").GetString()!;
        factory.Services.GetRequiredService<SupportDatabase>().Execute("DELETE FROM native_approval_requests WHERE operation_id = @id", ("@id", operationId));

        var (status, decided) = await api.DecideAsync(conversation, pending.GetProperty("approval_id").GetString()!, operationId, "approve");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Nothing has been changed", decided.ReplyText(), StringComparison.Ordinal);
        Assert.Equal("failed", (await api.GetOperationAsync(operationId)).GetProperty("status").GetString());
        Assert.Equal("processing", Order(factory, "ORD-1003"));
    }

    [Fact]
    public async Task A_finished_turn_is_never_stored_without_the_context_it_produced()
    {
        // Codex review on PR #77: the turn and the conversation's context are one transaction. If the context update
        // fails, the turn is not stored either, so a retry of the same client_turn_id runs again and sets the context.
        using var database = new TestDatabase();
        var clientTurnId = Guid.NewGuid().ToString();
        string conversation;
        await using (var host = Host(database))
        {
            var api = new ApiClient(host.CreateClient());
            conversation = await api.CreateConversationAsync();
            host.Services.GetRequiredService<SupportDatabase>().Execute(
                "CREATE TRIGGER stop_here BEFORE UPDATE OF context_json ON conversations BEGIN SELECT RAISE(ABORT, 'simulated stop'); END;");
            var status = await PostTurnStatusAsync(host, conversation, clientTurnId, "Where is my order ORD-1002?");
            Assert.Equal(HttpStatusCode.InternalServerError, status);
            host.Services.GetRequiredService<SupportDatabase>().Execute("DROP TRIGGER stop_here");
        }

        await using var restarted = Host(database);
        var restartedApi = new ApiClient(restarted.CreateClient());
        Assert.Equal(0, restarted.Services.GetRequiredService<SupportDatabase>().Query("SELECT COUNT(*) FROM turns", reader => reader.GetInt32(0))[0]);
        await restartedApi.AskAsync(conversation, "Where is my order ORD-1002?", clientTurnId);
        var followUp = await restartedApi.AskAsync(conversation, "When was it shipped?");
        Assert.Equal("answer", followUp.ReplyKind());
        Assert.Contains("ORD-1002", followUp.ReplyText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_approval_whose_turn_was_never_stored_cannot_run_and_a_retry_asks_once_more()
    {
        // Codex review on PR #77: the proposal was stored, then the process stopped before its turn was. The customer
        // never saw that approval; retrying the same submission asks again, and only the new approval is actionable.
        using var database = new TestDatabase();
        var clientTurnId = Guid.NewGuid().ToString();
        const string Message = "Please open a support ticket: my fridge shows error Z-77.";
        string conversation;
        await using (var host = Host(database))
        {
            var api = new ApiClient(host.CreateClient());
            conversation = await api.CreateConversationAsync();
            host.Services.GetRequiredService<SupportDatabase>().Execute(
                "CREATE TRIGGER stop_here BEFORE INSERT ON turns BEGIN SELECT RAISE(ABORT, 'simulated stop'); END;");
            Assert.Equal(HttpStatusCode.InternalServerError, await PostTurnStatusAsync(host, conversation, clientTurnId, Message));
            host.Services.GetRequiredService<SupportDatabase>().Execute("DROP TRIGGER stop_here");
        }

        await using var restarted = Host(database);
        var restartedApi = new ApiClient(restarted.CreateClient());
        var orphan = Assert.Single(restarted.Services.GetRequiredService<IOperationStore>().ForConversation(conversation));
        Assert.Empty((await restartedApi.GetConversationAsync(conversation)).GetProperty("pending_approvals").EnumerateArray());

        var pending = (await restartedApi.AskAsync(conversation, Message, clientTurnId)).GetProperty("pending_approval");

        Assert.NotEqual(orphan.OperationId, pending.GetProperty("operation_id").GetString());
        Assert.Single((await restartedApi.GetConversationAsync(conversation)).GetProperty("pending_approvals").EnumerateArray());
        Assert.Equal("expired", (await restartedApi.GetOperationAsync(orphan.OperationId)).GetProperty("status").GetString());
        var (orphanStatus, problem) = await restartedApi.DecideAsync(conversation, orphan.ApprovalId, orphan.OperationId, "approve");
        Assert.Equal(HttpStatusCode.Gone, orphanStatus);
        Assert.Equal("approval_expired", problem.GetProperty("code").GetString());
        await restartedApi.DecideAsync(conversation, pending.GetProperty("approval_id").GetString()!, pending.GetProperty("operation_id").GetString()!, "approve");
        Assert.Equal("tickets=1", State(restarted, "create_ticket"));
    }

    [Fact]
    public async Task A_decision_whose_turn_could_not_be_saved_can_be_retried_and_does_not_block_the_conversation()
    {
        // Codex review on PR #77: saving the decision turn fails after the write committed. The reservation is released,
        // so the retry answers from the operation record (no second write) and other turns are not stuck.
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var pending = (await api.AskAsync(conversation, "Please open a support ticket: my fridge shows error Z-77.")).GetProperty("pending_approval");
        var (approvalId, operationId) = (pending.GetProperty("approval_id").GetString()!, pending.GetProperty("operation_id").GetString()!);
        var database = factory.Services.GetRequiredService<SupportDatabase>();
        database.Execute("CREATE TRIGGER stop_here BEFORE INSERT ON turns BEGIN SELECT RAISE(ABORT, 'simulated failure'); END;");
        using (var response = await factory.CreateClient().PostAsync(
            new Uri($"/api/v1/conversations/{conversation}/approvals/{approvalId}", UriKind.Relative),
            System.Net.Http.Json.JsonContent.Create(new { operation_id = operationId, decision = "approve" }),
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        database.Execute("DROP TRIGGER stop_here");
        var (status, retried) = await api.DecideAsync(conversation, approvalId, operationId, "approve").WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("Done.", retried.ReplyText(), StringComparison.Ordinal);
        Assert.Equal("committed", (await api.GetOperationAsync(operationId)).GetProperty("status").GetString());
        Assert.Equal("tickets=1", State(factory, "create_ticket"));
        Assert.Equal("completed", (await api.AskAsync(conversation, "Where is my order ORD-1002?")).GetProperty("outcome").GetString());
    }

    /// <summary>Posts a turn and returns only the status: a failed turn's body is a developer error page, not JSON.</summary>
    private static async Task<HttpStatusCode> PostTurnStatusAsync(WebApplicationFactory<Program> host, string conversation, string clientTurnId, string message)
    {
        using var response = await host.CreateClient().PostAsync(
            new Uri($"/api/v1/conversations/{conversation}/turns", UriKind.Relative),
            System.Net.Http.Json.JsonContent.Create(new { client_turn_id = clientTurnId, message }),
            TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static WebApplicationFactory<Program> Host(TestDatabase database, TimeProvider? clock = null, IChatClient? chatClient = null) =>
        SupportApiFactory.Create(
            settings: database.Settings,
            principal: "CUST-1001",
            chatClient: chatClient,
            configureServices: clock is null ? null : services => services.Replace(ServiceDescriptor.Singleton(clock)));

    private static async Task<(string Conversation, string ApprovalId, string OperationId)> AskInOneHostAsync(TestDatabase database, string message, TimeProvider? clock = null)
    {
        await using var host = Host(database, clock);
        var api = new ApiClient(host.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var pending = (await api.AskAsync(conversation, message)).GetProperty("pending_approval");
        return (conversation, pending.GetProperty("approval_id").GetString()!, pending.GetProperty("operation_id").GetString()!);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Timed out waiting for the condition.");
    }

    private static IEnumerable<AITool> ToolsFor(string kind)
    {
        yield return AIFunctionFactory.Create(() => 0, kind);
    }

    private static string Order(WebApplicationFactory<Program> factory, string orderId) =>
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

    private static string State(WebApplicationFactory<Program> factory, string kind) => kind switch
    {
        "cancel_order" => Order(factory, "ORD-1003"),
        "refund_order" => $"refunds={factory.Services.GetRequiredService<IBillingRepository>().PaymentsFor("CUST-1001").Count(payment => payment.Kind == "refund")}",
        _ => $"tickets={factory.Services.GetRequiredService<ITicketStore>().OwnedBy("CUST-1001").Count}",
    };
}
