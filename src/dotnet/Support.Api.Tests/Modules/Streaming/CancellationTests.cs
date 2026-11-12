using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Support.Api.Common.Application.Streaming;
using Support.Api.Modules.Agents.Infrastructure.ModelProvider;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;
using Support.Api.Modules.Orders.Application;
using Support.Api.Modules.Technical.Application;
using Support.Api.Modules.Triage.Application;
using Support.Api.Modules.Triage.Domain;
using Support.Api.Modules.Triage.Infrastructure.Routing;

namespace Support.Api.Tests.Modules.Streaming;

/// <summary>
/// L12: cancellation is cooperative and only a cancel request triggers it. Seen before a write commits, nothing changes
/// and the turn ends cancelled; arriving after the commit, the committed result stands. A dropped connection is not a
/// cancellation: the turn finishes, and a status lookup plus duplicate protection recover its result.
/// </summary>
public sealed class CancellationTests
{
    [Fact]
    public async Task A_running_turn_reports_running_and_stops_when_cancelled()
    {
        var principals = new SwitchablePrincipalSource("CUST-1001");
        var model = new GatedModel();
        await using var factory = SupportApiFactory.Create(principalSource: principals, chatClient: model.Client);
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        var asking = api.SendTurnAsync(conversation, new { client_turn_id = Guid.NewGuid().ToString(), message = "Where is my order ORD-1002?" });
        await model.FirstCallStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var turnId = (await api.GetConversationAsync(conversation)).GetProperty("active_turn_id").GetString()!;

        var (_, running) = await api.SendAsync(HttpMethod.Get, $"/api/v1/conversations/{conversation}/turns/{turnId}");
        ContractSchemas.AssertValid("turn-result", running);
        Assert.Equal("running", running.GetProperty("state").GetString());

        principals.SwitchTo("CUST-1002");
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Post, $"/api/v1/conversations/{conversation}/turns/{turnId}/cancel")).Status);
        principals.SwitchTo("CUST-1001");
        var (accepted, cancelResponse) = await api.SendAsync(HttpMethod.Post, $"/api/v1/conversations/{conversation}/turns/{turnId}/cancel");
        Assert.Equal(HttpStatusCode.Accepted, accepted);
        ContractSchemas.AssertValid("cancel-turn-response", cancelResponse);
        model.Release();

        var (status, finished) = await asking;
        Assert.Equal(HttpStatusCode.OK, status);
        ContractSchemas.AssertValid("turn-result", finished);
        Assert.Equal(turnId, finished.GetProperty("turn_id").GetString());
        Assert.Equal("cancelled", finished.GetProperty("outcome").GetString());
        var (again, problem) = await api.SendAsync(HttpMethod.Post, $"/api/v1/conversations/{conversation}/turns/{turnId}/cancel");
        Assert.Equal(HttpStatusCode.Conflict, again);
        Assert.Equal("turn_already_finished", problem.GetProperty("code").GetString());
        Assert.Equal("completed", (await api.AskAsync(conversation, "Where is my order ORD-1002?")).GetProperty("outcome").GetString()); // not stuck
    }

    [Fact]
    public async Task A_cancel_seen_before_the_write_commits_changes_nothing()
    {
        var router = new HeldResume();
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001", configureServices: services =>
            services.Replace(ServiceDescriptor.Singleton<ISupportRouter>(provider => router.Wrap(provider.GetRequiredService<SupportWorkflowRouter>()))));
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var pending = (await api.AskAsync(conversation, "Please open a support ticket: my fridge shows error Z-77.")).GetProperty("pending_approval");
        var operationId = pending.GetProperty("operation_id").GetString()!;

        var deciding = api.DecideAsync(conversation, pending.GetProperty("approval_id").GetString()!, operationId, "approve");
        await router.Entered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var turnId = (await api.GetConversationAsync(conversation)).GetProperty("active_turn_id").GetString()!;
        Assert.Equal(HttpStatusCode.Accepted, (await api.SendAsync(HttpMethod.Post, $"/api/v1/conversations/{conversation}/turns/{turnId}/cancel")).Status);
        router.Release();

        var (status, decided) = await deciding;
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("cancelled", decided.GetProperty("outcome").GetString());
        var operation = await api.GetOperationAsync(operationId);
        Assert.Equal("failed", operation.GetProperty("status").GetString());
        Assert.Equal("cancelled", operation.GetProperty("failure").GetProperty("code").GetString());
        Assert.Empty(factory.Services.GetRequiredService<ITicketStore>().OwnedBy("CUST-1001"));
    }

    [Fact]
    public async Task The_write_itself_checks_cancellation_before_its_transaction_and_applies_nothing()
    {
        // The defined pre-commit boundary, directly: an approved operation, a cancelled token, no ticket, still running.
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var pending = (await api.AskAsync(conversation, "Please open a support ticket: my fridge shows error Z-77.")).GetProperty("pending_approval");
        var store = factory.Services.GetRequiredService<IOperationStore>();
        var operation = store.Find(pending.GetProperty("operation_id").GetString()!)!;
        Assert.True(operation.TryDecide(ApprovalDecision.Approve, DateTimeOffset.UtcNow) && store.TryUpdate(operation, OperationStatus.AwaitingApproval));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        using (ApprovalScope.Begin(operation))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                factory.Services.GetRequiredService<OperationService>().ExecuteApprovedAsync(OperationKind.CreateTicket, operation.Arguments, cancelled.Token));
        }

        Assert.Empty(factory.Services.GetRequiredService<ITicketStore>().OwnedBy("CUST-1001"));
        Assert.Equal(OperationStatus.Running, store.Find(operation.OperationId)!.Status);
    }

    [Fact]
    public async Task A_cancel_that_arrives_after_the_commit_leaves_the_committed_result_standing()
    {
        using var hang = new ManualResetEventSlim();
        var model = HangAfterTheWrite(hang);
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001", chatClient: model.Client());
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var pending = (await api.AskAsync(conversation, "Please cancel order ORD-1003.")).GetProperty("pending_approval");
        var operationId = pending.GetProperty("operation_id").GetString()!;

        var deciding = api.DecideAsync(conversation, pending.GetProperty("approval_id").GetString()!, operationId, "approve");
        await WaitForAsync(() => factory.Services.GetRequiredService<IOperationStore>().Find(operationId)!.Status == OperationStatus.Committed);
        var turnId = (await api.GetConversationAsync(conversation)).GetProperty("active_turn_id").GetString()!;
        Assert.Equal(HttpStatusCode.Accepted, (await api.SendAsync(HttpMethod.Post, $"/api/v1/conversations/{conversation}/turns/{turnId}/cancel")).Status);
        hang.Set();

        var (status, decided) = await deciding;
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("completed", decided.GetProperty("outcome").GetString()); // the factual result, not "cancelled"
        Assert.Equal("approval_result", decided.ReplyKind());
        Assert.Equal("committed", (await api.GetOperationAsync(operationId)).GetProperty("status").GetString());
        Assert.Equal("cancelled", Order(factory, "ORD-1003"));
    }

    [Fact]
    public async Task A_lost_final_event_is_recovered_by_status_lookup_and_a_repeat_never_writes_twice()
    {
        // The client drops the stream while the approved write runs. That is not a cancellation: the turn finishes.
        using var hang = new ManualResetEventSlim();
        var model = HangAfterTheWrite(hang);
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001", chatClient: model.Client());
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var pending = (await api.AskAsync(conversation, "Please open a support ticket: my fridge shows error Z-77.")).GetProperty("pending_approval");
        var (approvalId, operationId) = (pending.GetProperty("approval_id").GetString()!, pending.GetProperty("operation_id").GetString()!);

        string turnId;
        using (var disconnect = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/conversations/{conversation}/approvals/{approvalId}", UriKind.Relative))
            {
                Content = JsonContent.Create(new { operation_id = operationId, decision = "approve" }),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = await api.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, disconnect.Token);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(disconnect.Token));
            string? line;
            while ((line = await reader.ReadLineAsync(disconnect.Token)) is not null && !line.StartsWith("data: ", StringComparison.Ordinal))
            {
            }

            turnId = JsonElement.Parse(line![6..]).GetProperty("turn_id").GetString()!;
            await WaitForAsync(() => factory.Services.GetRequiredService<IOperationStore>().Find(operationId)!.Status == OperationStatus.Committed);
            await disconnect.CancelAsync(); // the connection drops before turn.final
        }

        var (_, stillRunning) = await api.SendAsync(HttpMethod.Get, $"/api/v1/conversations/{conversation}/turns/{turnId}");
        Assert.Equal("running", stillRunning.GetProperty("state").GetString());
        hang.Set();

        JsonElement recovered = default;
        await WaitForAsync(() => (recovered = api.SendAsync(HttpMethod.Get, $"/api/v1/conversations/{conversation}/turns/{turnId}").GetAwaiter().GetResult().Body).GetProperty("state").GetString() == "finished");
        ContractSchemas.AssertValid("turn-result", recovered);
        Assert.Equal("completed", recovered.GetProperty("outcome").GetString());
        Assert.Equal("committed", (await api.GetOperationAsync(operationId)).GetProperty("status").GetString());
        var (repeatStatus, repeated) = await api.DecideAsync(conversation, approvalId, operationId, "approve");
        Assert.Equal(HttpStatusCode.OK, repeatStatus);
        Assert.Equal(turnId, repeated.GetProperty("turn_id").GetString());
        Assert.Single(factory.Services.GetRequiredService<ITicketStore>().OwnedBy("CUST-1001"));
    }

    [Fact]
    public async Task A_dropped_plain_request_is_not_a_cancellation_and_the_turn_still_finishes()
    {
        var model = new GatedModel();
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001", chatClient: model.Client);
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        using (var disconnect = new CancellationTokenSource())
        {
            var asking = api.Http.PostAsJsonAsync(
                new Uri($"/api/v1/conversations/{conversation}/turns", UriKind.Relative),
                new { client_turn_id = Guid.NewGuid().ToString(), message = "Where is my order ORD-1002?" },
                disconnect.Token);
            await model.FirstCallStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await disconnect.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
        }

        var turnId = (await api.GetConversationAsync(conversation)).GetProperty("active_turn_id").GetString()!;
        model.Release();
        JsonElement finished = default;
        await WaitForAsync(() => (finished = api.SendAsync(HttpMethod.Get, $"/api/v1/conversations/{conversation}/turns/{turnId}").GetAwaiter().GetResult().Body).GetProperty("state").GetString() == "finished");
        Assert.Equal("completed", finished.GetProperty("outcome").GetString());
        Assert.Equal("answer", finished.ReplyKind());
    }

    [Fact]
    public async Task A_streamed_turn_cancelled_mid_run_ends_with_a_cancelled_final_event()
    {
        var model = new GatedModel();
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001", chatClient: model.Client);
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        var streaming = SseClient.PostAsync(api.Http, $"/api/v1/conversations/{conversation}/turns", new { client_turn_id = Guid.NewGuid().ToString(), message = "Where is my order ORD-1002?" });
        await model.FirstCallStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var turnId = (await api.GetConversationAsync(conversation)).GetProperty("active_turn_id").GetString()!;
        await api.SendAsync(HttpMethod.Post, $"/api/v1/conversations/{conversation}/turns/{turnId}/cancel");
        model.Release();

        var final = SseClient.Final((await streaming).Events);
        Assert.Equal("cancelled", final.GetProperty("outcome").GetString());
        Assert.Equal(final.ReplyText(), SseClient.CountedText((await streaming).Events));
    }

    [Fact]
    public async Task Cancelling_an_unknown_turn_is_not_found()
    {
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        var (status, problem) = await api.SendAsync(HttpMethod.Post, $"/api/v1/conversations/{conversation}/turns/turn_unknown/cancel");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", problem.GetProperty("code").GetString());
    }

    /// <summary>A model that requests the write normally, then hangs on the call that follows the write's result.</summary>
    private static CountingScript HangAfterTheWrite(ManualResetEventSlim hang) => new(messages =>
    {
        if (messages[^1].Contents.OfType<FunctionResultContent>().Any())
        {
            hang.Wait(TimeSpan.FromSeconds(30));
        }

        var text = messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var tool = text.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? FakeChatClient.CancelOrderToolName : FakeChatClient.CreateTicketToolName;
        return FakeChatClient.DefaultScript(messages, new ChatOptions { Tools = [AIFunctionFactory.Create(() => 0, tool)] });
    });

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400 && !condition(); attempt++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Timed out waiting for the condition.");
    }

    private static string Order(WebApplicationFactory<Program> factory, string orderId) =>
        factory.Services.GetRequiredService<IOrderRepository>().Find(orderId)!.Status;

    /// <summary>Holds a resumed approval run at its start, before anything runs, until the test releases it.</summary>
    private sealed class HeldResume
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => entered.Task;

        public void Release() => release.TrySetResult();

        public ISupportRouter Wrap(ISupportRouter inner) => new Router(inner, this);

        private sealed class Router(ISupportRouter inner, HeldResume held) : ISupportRouter
        {
            public Task<SupportReply> RouteAsync(RoutingRequest request, ITurnObserver observer, CancellationToken cancellationToken) =>
                inner.RouteAsync(request, observer, cancellationToken);

            public async Task<SupportReply> ResumeAsync(ApprovalResume decided, ITurnObserver observer, CancellationToken cancellationToken)
            {
                held.entered.TrySetResult();
                await held.release.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                return await inner.ResumeAsync(decided, observer, cancellationToken);
            }
        }
    }
}
