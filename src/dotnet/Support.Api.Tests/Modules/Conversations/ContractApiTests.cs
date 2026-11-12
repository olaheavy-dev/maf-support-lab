using System.Net;

namespace Support.Api.Tests.Modules.Conversations;

/// <summary>The version-1 HTTP behavior from contracts/README.md, checked against the shared schemas.</summary>
public sealed class ContractApiTests
{
    [Fact]
    public async Task Request_with_an_identity_field_is_rejected()
    {
        await using var factory = SupportApiFactory.Create();
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        var (status, problem) = await api.SendTurnAsync(conversation, new { client_turn_id = Guid.NewGuid().ToString(), message = "Where is ORD-2001?", customer_id = "CUST-1002" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_request", problem.GetProperty("code").GetString());
        Assert.Equal("/customer_id", problem.GetProperty("errors")[0].GetProperty("pointer").GetString());
    }

    /// <summary>
    /// Found by the cross-backend probes: an invalid create body lists what is wrong as a JSON pointer, as the
    /// contract requires for invalid_request; "" is the whole body, and a key is escaped (RFC 6901).
    /// </summary>
    [Theory]
    [InlineData("{\"customer_id\":\"CUST-1002\"}", "/customer_id")]
    [InlineData("{\"a/b~c\":1}", "/a~1b~0c")]
    [InlineData("{not json", "")]
    [InlineData("[]", "")]
    public async Task An_invalid_create_body_lists_the_offending_pointer(string body, string expectedPointer)
    {
        await using var factory = SupportApiFactory.Create();
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        using var response = await factory.CreateClient().PostAsync(new Uri("/api/v1/conversations", UriKind.Relative), content, TestContext.Current.CancellationToken);
        using var problem = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        ContractSchemas.AssertValid("problem", problem.RootElement);
        Assert.Equal(expectedPointer, problem.RootElement.GetProperty("errors")[0].GetProperty("pointer").GetString());
    }

    [Theory]
    [InlineData("not-a-uuid", "Where is ORD-1002?", "/client_turn_id")]
    [InlineData("3f2c8a1e-5b7d-4c9a-8e2f-1a6b9c0d4e71", "   ", "/message")]
    public async Task Invalid_fields_return_a_contract_problem(string clientTurnId, string message, string expectedPointer)
    {
        await using var factory = SupportApiFactory.Create();
        var api = new ApiClient(factory.CreateClient());

        var (status, problem) = await api.SendTurnAsync(await api.CreateConversationAsync(), new { client_turn_id = clientTurnId, message });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(expectedPointer, problem.GetProperty("errors")[0].GetProperty("pointer").GetString());
    }

    [Fact]
    public async Task Resending_the_same_client_turn_id_returns_the_original_turn_without_running_again()
    {
        var model = new CountingScript();
        await using var factory = SupportApiFactory.Create(chatClient: model.Client());
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var clientTurnId = Guid.NewGuid().ToString();

        var first = await api.AskAsync(conversation, "Where is my order ORD-1002?", clientTurnId);
        var callsAfterFirst = model.Calls;
        var second = await api.AskAsync(conversation, "Where is my order ORD-1002?", clientTurnId);

        Assert.Equal(first.GetProperty("turn_id").GetString(), second.GetProperty("turn_id").GetString());
        Assert.Equal(callsAfterFirst, model.Calls);
    }

    [Fact]
    public async Task Reusing_a_client_turn_id_with_a_different_message_is_a_conflict()
    {
        await using var factory = SupportApiFactory.Create();
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var clientTurnId = Guid.NewGuid().ToString();
        await api.AskAsync(conversation, "Where is my order ORD-1002?", clientTurnId);

        var (status, problem) = await api.SendTurnAsync(conversation, new { client_turn_id = clientTurnId, message = "Where is my order ORD-1003?" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("client_turn_id_reused", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Another_customers_conversation_and_turns_are_not_found()
    {
        var principals = new SwitchablePrincipalSource("CUST-1001");
        await using var factory = SupportApiFactory.Create(principalSource: principals);
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var turn = await api.AskAsync(conversation, "Where is my order ORD-1002?");
        var turnId = turn.GetProperty("turn_id").GetString();

        principals.SwitchTo("CUST-1002");

        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/v1/conversations/{conversation}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/v1/conversations/{conversation}/turns/{turnId}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendTurnAsync(conversation, new { client_turn_id = Guid.NewGuid().ToString(), message = "hi" })).Status);
    }

    [Fact]
    public async Task Owner_can_fetch_a_finished_turn()
    {
        await using var factory = SupportApiFactory.Create();
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();
        var turn = await api.AskAsync(conversation, "Where is my order ORD-1003?");

        var (status, fetched) = await api.SendAsync(HttpMethod.Get, $"/api/v1/conversations/{conversation}/turns/{turn.GetProperty("turn_id").GetString()}");

        Assert.Equal(HttpStatusCode.OK, status);
        ContractSchemas.AssertValid("turn-result", fetched);
        Assert.Equal(turn.ReplyText(), fetched.ReplyText());
    }

    [Fact]
    public async Task Create_conversation_rejects_owner_or_backend_fields()
    {
        await using var factory = SupportApiFactory.Create();
        var api = new ApiClient(factory.CreateClient());

        var (status, _) = await api.SendAsync(HttpMethod.Post, "/api/v1/conversations", new { owner = "CUST-1002" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }
}
