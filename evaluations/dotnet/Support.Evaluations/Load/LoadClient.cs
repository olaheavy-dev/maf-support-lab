using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Support.Evaluations.Load;

/// <summary>One timed request: the status, the parsed body (when JSON), and how long it took.</summary>
internal sealed record Timed(int Status, JsonObject? Body, double Milliseconds)
{
    public string? Text(params string[] path)
    {
        JsonNode? node = Body;
        foreach (var name in path)
        {
            node = node is JsonObject obj ? obj[name] : null;
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}

/// <summary>
/// The contract API as the load scenarios use it: black-box HTTP, JSON responses, every call timed. A backend
/// process serves one customer, so a client is per customer (base URL).
/// </summary>
internal sealed class LoadClient(HttpClient http)
{
    public async Task<Timed> CreateConversationAsync(Uri baseUrl, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Post, new Uri(baseUrl, "api/v1/conversations"), new JsonObject(), cancellationToken).ConfigureAwait(false);

    public async Task<Timed> TurnAsync(Uri baseUrl, string conversation, string message, string? clientTurnId, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Post, new Uri(baseUrl, $"api/v1/conversations/{conversation}/turns"), new JsonObject { ["client_turn_id"] = clientTurnId ?? Guid.NewGuid().ToString(), ["message"] = message }, cancellationToken).ConfigureAwait(false);

    public async Task<Timed> DecideAsync(Uri baseUrl, string conversation, string approvalId, string operationId, string decision, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Post, new Uri(baseUrl, $"api/v1/conversations/{conversation}/approvals/{approvalId}"), new JsonObject { ["operation_id"] = operationId, ["decision"] = decision }, cancellationToken).ConfigureAwait(false);

    public async Task<Timed> CancelAsync(Uri baseUrl, string conversation, string turnId, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Post, new Uri(baseUrl, $"api/v1/conversations/{conversation}/turns/{turnId}/cancel"), null, cancellationToken).ConfigureAwait(false);

    public async Task<Timed> GetAsync(Uri url, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);

    private async Task<Timed> SendAsync(HttpMethod method, Uri url, JsonObject? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null || method == HttpMethod.Post)
        {
            request.Content = new StringContent((body ?? new JsonObject()).ToJsonString(), Encoding.UTF8, "application/json");
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var clock = Stopwatch.StartNew();
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        clock.Stop();
        JsonObject? parsed = null;
        try
        {
            parsed = text.Length > 0 ? JsonNode.Parse(text) as JsonObject : null;
        }
        catch (JsonException)
        {
            // Not JSON: the status still counts.
        }

        return new Timed((int)response.StatusCode, parsed, clock.Elapsed.TotalMilliseconds);
    }
}
