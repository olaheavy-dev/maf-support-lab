using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Domain;
using Support.Api.Modules.Agents.Infrastructure.ModelProvider;
using Support.Api.Modules.Orders.Application.GetOrderStatus;
using Support.Api.Modules.Orders.Domain;

namespace Support.Api.Tests.Modules.Resilience;

/// <summary>
/// Every fault has a bounded number of attempts, a bounded time, and a safe contract outcome. Model calls have
/// exactly one retry owner (the Anthropic SDK: MaxRetries = 2, so at most 3 HTTP attempts per call); the application
/// never retries a turn; loops and slow tools hit per-turn limits; cancellation stops new attempts.
/// </summary>
public sealed class ResilienceTests
{
    [Fact]
    public async Task A_transient_provider_error_is_retried_by_the_SDK_and_the_turn_succeeds()
    {
        var http = new FakeAnthropicHttp(attempt => attempt <= 2 ? FakeAnthropicHttp.Error(500, "api_error") : null);
        await using var factory = LiveOverFakeHttp(http);
        var api = new ApiClient(factory.CreateClient());

        var turn = await api.AskAsync(await api.CreateConversationAsync(), "Where is my order ORD-1002?");

        Assert.Equal("completed", turn.GetProperty("outcome").GetString());
        Assert.Equal(4, http.Attempts); // triage: 2 failures + 1 success; specialist: 1
    }

    [Fact]
    public async Task A_permanent_provider_error_is_not_retried_and_fails_safely()
    {
        var http = new FakeAnthropicHttp(_ => FakeAnthropicHttp.Error(400, "invalid_request_error"));
        await using var factory = LiveOverFakeHttp(http);
        var api = new ApiClient(factory.CreateClient());

        var turn = await api.AskAsync(await api.CreateConversationAsync(), "Where is my order ORD-1002?");

        Assert.Equal("failed", turn.GetProperty("outcome").GetString());
        Assert.Equal("internal_error", turn.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, http.Attempts);
    }

    [Fact]
    public async Task A_provider_that_stays_down_gets_a_bounded_number_of_attempts()
    {
        var http = new FakeAnthropicHttp(_ => FakeAnthropicHttp.Error(500, "api_error"));
        await using var factory = LiveOverFakeHttp(http);
        var api = new ApiClient(factory.CreateClient());

        var turn = await api.AskAsync(await api.CreateConversationAsync(), "Where is my order ORD-1002?");

        Assert.Equal("failed", turn.GetProperty("outcome").GetString());
        Assert.Equal("model_unavailable", turn.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(3, http.Attempts); // MaxRetries 2: no second layer multiplies this, and no further model call is made
    }

    [Fact]
    public async Task A_rate_limit_is_retried_after_the_providers_hint_and_reported_with_it()
    {
        var http = new FakeAnthropicHttp(_ => FakeAnthropicHttp.Error(429, "rate_limit_error", retryAfterSeconds: 1));
        await using var factory = LiveOverFakeHttp(http);
        var api = new ApiClient(factory.CreateClient());

        var turn = await api.AskAsync(await api.CreateConversationAsync(), "Where is my order ORD-1002?");

        Assert.Equal("failed", turn.GetProperty("outcome").GetString());
        var error = turn.GetProperty("error");
        Assert.Equal("rate_limited", error.GetProperty("code").GetString());
        Assert.Equal(1, error.GetProperty("retry_after_seconds").GetInt32());
        Assert.Equal(3, http.Attempts);
        var times = http.AttemptTimes;
        Assert.True(times[1] - times[0] >= TimeSpan.FromMilliseconds(900), "The SDK waits for the retry-after hint before retrying.");
    }

    [Fact]
    public async Task A_provider_that_never_answers_times_out_per_attempt_and_fails_as_a_timeout()
    {
        var http = new FakeAnthropicHttp(_ => null) { BeforeAttempt = _ => Task.Delay(Timeout.Infinite) };
        await using var factory = LiveOverFakeHttp(http, new() { ["Resilience:ModelAttemptTimeoutSeconds"] = "1", ["Resilience:ModelMaxRetries"] = "1" });
        var api = new ApiClient(factory.CreateClient());
        var clock = Stopwatch.StartNew();

        var turn = await api.AskAsync(await api.CreateConversationAsync(), "Where is my order ORD-1002?");

        Assert.Equal("failed", turn.GetProperty("outcome").GetString());
        Assert.Equal("timeout", turn.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(2, http.Attempts);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Bounded latency, took {clock.Elapsed}.");
    }

    [Fact]
    public async Task Cancelling_during_a_retry_stops_new_attempts()
    {
        // The stretch: a transient failure, then a cancel while the SDK is backing off. No further attempt is made.
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new FakeAnthropicHttp(attempt =>
        {
            failed.TrySetResult();
            return FakeAnthropicHttp.Error(500, "api_error");
        });
        await using var factory = LiveOverFakeHttp(http, new() { ["Resilience:ModelMaxRetries"] = "5" });
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        var asking = api.SendTurnAsync(conversation, new { client_turn_id = Guid.NewGuid().ToString(), message = "Where is my order ORD-1002?" });
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var turnId = (await api.GetConversationAsync(conversation)).GetProperty("active_turn_id").GetString()!;
        await api.SendAsync(HttpMethod.Post, $"/api/v1/conversations/{conversation}/turns/{turnId}/cancel");
        var (_, turn) = await asking;
        var attemptsAtEnd = http.Attempts;
        await Task.Delay(1500, TestContext.Current.CancellationToken); // longer than the SDK's next backoff

        Assert.Equal("cancelled", turn.GetProperty("outcome").GetString());
        Assert.InRange(attemptsAtEnd, 1, 2); // a request already in flight may finish; nothing new starts
        Assert.Equal(attemptsAtEnd, http.Attempts);
    }

    [Fact]
    public async Task A_model_that_loops_on_tools_is_stopped_by_the_turn_budget()
    {
        // The model calls get_order_status forever. The turn's model-call budget ends it, whatever the model does.
        var model = new CountingScript(_ => FakeChatClient.ToolCall(FakeChatClient.OrderStatusToolName, new Dictionary<string, object?> { ["orderId"] = "ORD-1002" }));
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001", chatClient: model.Client(), settings: new Dictionary<string, string?>
        {
            ["Resilience:MaxModelCallsPerTurn"] = "4",
            ["Resilience:MaxToolIterations"] = "50",
        });
        var api = new ApiClient(factory.CreateClient());

        var turn = await api.AskAsync(await api.CreateConversationAsync(), "Where is my order ORD-1002?");

        Assert.Equal("failed", turn.GetProperty("outcome").GetString());
        Assert.Equal("budget_exhausted", turn.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(3, model.Calls); // triage used 1 of the 4; the specialist got 3; the 5th call was never made
    }

    [Fact]
    public async Task The_tool_step_limit_bounds_one_agent_run()
    {
        // With a generous turn budget, MAF's function-invocation loop stops the run after MaxToolIterations steps.
        var model = new CountingScript(_ => FakeChatClient.ToolCall(FakeChatClient.OrderStatusToolName, new Dictionary<string, object?> { ["orderId"] = "ORD-1002" }));
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001", chatClient: model.Client(), settings: new Dictionary<string, string?>
        {
            ["Resilience:MaxModelCallsPerTurn"] = "100",
            ["Resilience:MaxToolIterations"] = "3",
        });
        var api = new ApiClient(factory.CreateClient());

        var turn = await api.AskAsync(await api.CreateConversationAsync(), "Where is my order ORD-1002?");

        Assert.InRange(model.Calls, 3, 4); // bounded by the step limit, not by the model
        Assert.True(turn.GetProperty("outcome").GetString() is "completed" or "failed");
    }

    [Fact]
    public async Task A_tool_that_hangs_is_cut_off_and_the_turn_fails_as_tool_unavailable()
    {
        await using var factory = SupportApiFactory.Create(
            principal: "CUST-1001",
            settings: new Dictionary<string, string?> { ["Resilience:ToolTimeoutSeconds"] = "1" },
            configureServices: services => services.Replace(ServiceDescriptor.Singleton<IQueryHandler<GetOrderStatusQuery, Order>>(new HangingLookup())));
        var api = new ApiClient(factory.CreateClient());
        var clock = Stopwatch.StartNew();

        var turn = await api.AskAsync(await api.CreateConversationAsync(), "Where is my order ORD-1002?");

        Assert.Equal("failed", turn.GetProperty("outcome").GetString());
        Assert.Equal("tool_unavailable", turn.GetProperty("error").GetProperty("code").GetString());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Bounded latency, took {clock.Elapsed}.");
    }

    [Fact]
    public async Task A_turn_that_runs_past_its_deadline_fails_as_a_timeout()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var model = new GatedModel();
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001", chatClient: model.Client, configureServices: services =>
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        var asking = api.AskAsync(conversation, "Where is my order ORD-1002?");
        await model.FirstCallStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(91)); // past the 90-second turn deadline
        model.Release();
        var turn = await asking;

        Assert.Equal("failed", turn.GetProperty("outcome").GetString());
        Assert.Equal("timeout", turn.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("Resilience:TurnTimeoutSeconds", "-1")]
    [InlineData("Resilience:TurnTimeoutSeconds", "99999999")]
    [InlineData("Resilience:MaxModelCallsPerTurn", "0")]
    [InlineData("Resilience:ModelMaxRetries", "-1")]
    public async Task Limits_that_would_break_or_unbound_a_turn_are_refused_at_startup(string setting, string value)
    {
        // Codex review on PR #81: an impossible deadline must stop the app at startup, not fault a turn later.
        await using var factory = SupportApiFactory.Create(settings: new Dictionary<string, string?> { [setting] = value });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(setting.Split(':')[1], error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_while_a_turn_sets_up_its_limits_releases_the_conversation()
    {
        // Codex review on PR #81: anything that fails before the run's own handlers must not leave the turn reserved.
        var timers = new FailingTimers();
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001", configureServices: services =>
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(timers)));
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        timers.Fail = true; // the turn deadline's timer cannot be created
        HttpStatusCode status;
        using (var response = await factory.CreateClient().PostAsync(
            new Uri($"/api/v1/conversations/{conversation}/turns", UriKind.Relative),
            System.Net.Http.Json.JsonContent.Create(new { client_turn_id = Guid.NewGuid().ToString(), message = "Where is my order ORD-1002?" }),
            TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
        {
            status = response.StatusCode; // a 500 body is the developer error page, not JSON
        }

        timers.Fail = false;

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await api.GetConversationAsync(conversation)).GetProperty("active_turn_id").ValueKind);
        Assert.Equal("completed", (await api.AskAsync(conversation, "Where is my order ORD-1002?")).GetProperty("outcome").GetString());
    }

    private static WebApplicationFactory<Program> LiveOverFakeHttp(FakeAnthropicHttp http, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ModelProvider:Mode"] = "Live",
            ["ModelProvider:Anthropic:Model"] = "claude-haiku-5-5",
            ["ModelProvider:Anthropic:ApiKey"] = "test-key-not-real",
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        return SupportApiFactory.Create(settings: settings, principal: "CUST-1001", configureServices: services =>
            services.AddKeyedSingleton<HttpMessageHandler>(ModelProviderRegistration.ProviderHttpHandlerKey, http));
    }

    /// <summary>The system clock, except that creating a timer can be made to fail.</summary>
    private sealed class FailingTimers : TimeProvider
    {
        public volatile bool Fail;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Fail ? throw new InvalidOperationException("Simulated timer failure.") : System.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>An order lookup that never answers (until cancelled).</summary>
    private sealed class HangingLookup : IQueryHandler<GetOrderStatusQuery, Order>
    {
        public async Task<Result<Order>> Handle(GetOrderStatusQuery query, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
