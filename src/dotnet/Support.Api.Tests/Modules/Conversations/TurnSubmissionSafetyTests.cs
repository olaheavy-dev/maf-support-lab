using System.Net;
using Microsoft.Extensions.AI;
using Support.Api.Modules.Agents.Infrastructure.ModelProvider;

namespace Support.Api.Tests.Modules.Conversations;

/// <summary>Codex review findings on PR #69: null fields and concurrent duplicate submissions.</summary>
public sealed class TurnSubmissionSafetyTests
{
    [Theory]
    [InlineData("""{"client_turn_id":null,"message":"Where is ORD-1002?"}""", "/client_turn_id")]
    [InlineData("""{"client_turn_id":"3f2c8a1e-5b7d-4c9a-8e2f-1a6b9c0d4e71","message":null}""", "/message")]
    public async Task Null_fields_are_a_contract_problem_not_a_server_error(string json, string expectedPointer)
    {
        await using var factory = SupportApiFactory.Create();
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await api.Http.PostAsync(new Uri($"/api/v1/conversations/{conversation}/turns", UriKind.Relative), content, TestContext.Current.CancellationToken);
        var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        ContractSchemas.AssertValid("problem", body);
        Assert.Equal(expectedPointer, body.GetProperty("errors")[0].GetProperty("pointer").GetString());
    }

    [Fact]
    public async Task Concurrent_duplicates_run_the_agent_once_and_share_the_result()
    {
        var gate = new GatedModel();
        await using var factory = SupportApiFactory.Create(chatClient: gate.Client);
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var request = new { client_turn_id = Guid.NewGuid().ToString(), message = "Where is my order ORD-1002?" };

        var first = api.SendTurnAsync(conversation, request);
        await gate.FirstCallStarted.WaitAsync(TestContext.Current.CancellationToken);
        var second = api.SendTurnAsync(conversation, request);
        await Task.Delay(200, TestContext.Current.CancellationToken); // let the duplicate reach the server while the first is still running
        gate.Release();

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(HttpStatusCode.OK, result.Status));
        Assert.Equal(results[0].Body.GetProperty("turn_id").GetString(), results[1].Body.GetProperty("turn_id").GetString());
        Assert.Equal(2, gate.Calls); // one tool request + one answer: a single agent run
    }

    [Fact]
    public async Task Concurrent_reuse_with_a_different_message_is_a_conflict()
    {
        var gate = new GatedModel();
        await using var factory = SupportApiFactory.Create(chatClient: gate.Client);
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var clientTurnId = Guid.NewGuid().ToString();

        var first = api.SendTurnAsync(conversation, new { client_turn_id = clientTurnId, message = "Where is my order ORD-1002?" });
        await gate.FirstCallStarted.WaitAsync(TestContext.Current.CancellationToken);
        var (status, problem) = await api.SendTurnAsync(conversation, new { client_turn_id = clientTurnId, message = "Where is my order ORD-1003?" });
        gate.Release();
        await first;

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("client_turn_id_reused", problem.GetProperty("code").GetString());
    }

    /// <summary>Fake model that holds the first call until the test releases it.</summary>
    private sealed class GatedModel
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public GatedModel() => Client = new FakeChatClient(messages =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                release.Task.Wait(TimeSpan.FromSeconds(10));
            }

            return FakeChatClient.DefaultScript(messages);
        });

        public IChatClient Client { get; }

        public int Calls => calls;

        public Task FirstCallStarted => started.Task;

        public void Release() => release.TrySetResult();
    }
}
