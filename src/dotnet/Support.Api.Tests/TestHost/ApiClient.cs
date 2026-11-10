using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Support.Api.Tests.TestHost;

/// <summary>Small helper for calling the version-1 API and checking every body against the contract.</summary>
internal sealed class ApiClient(HttpClient http)
{
    public HttpClient Http { get; } = http;

    public async Task<string> CreateConversationAsync()
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/v1/conversations", new { });
        Assert.Equal(HttpStatusCode.Created, status);
        ContractSchemas.AssertValid("conversation", body);
        return body.GetProperty("conversation_id").GetString()!;
    }

    public async Task<JsonElement> AskAsync(string conversationId, string message, string? clientTurnId = null)
    {
        var (status, body) = await SendTurnAsync(conversationId, new { client_turn_id = clientTurnId ?? Guid.NewGuid().ToString(), message });
        Assert.Equal(HttpStatusCode.OK, status);
        ContractSchemas.AssertValid("turn-result", body);
        return body;
    }

    public Task<(HttpStatusCode Status, JsonElement Body)> SendTurnAsync(string conversationId, object body) =>
        SendAsync(HttpMethod.Post, $"/api/v1/conversations/{conversationId}/turns", body);

    public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await Http.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        if (response.Content.Headers.ContentType?.MediaType == "application/problem+json")
        {
            ContractSchemas.AssertValid("problem", json);
        }

        return (response.StatusCode, json);
    }
}

internal static class TurnJson
{
    public static string ReplyKind(this JsonElement turn) => turn.GetProperty("reply").GetProperty("kind").GetString()!;

    public static string ReplyText(this JsonElement turn) => turn.GetProperty("reply").GetProperty("text").GetString()!;
}
