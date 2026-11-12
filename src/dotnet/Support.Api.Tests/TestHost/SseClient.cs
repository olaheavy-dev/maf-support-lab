using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Support.Api.Tests.TestHost;

/// <summary>
/// Reads a turn's Server-Sent Events and checks the transport rules on every event: the SSE event name equals the
/// payload type, the SSE id equals its sequence, sequences count up from 1, and each payload matches stream-event.
/// </summary>
internal static class SseClient
{
    public static async Task<(HttpStatusCode Status, string? ContentType, IReadOnlyList<JsonElement> Events, JsonElement? Problem)> PostAsync(HttpClient http, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        return contentType == "text/event-stream"
            ? (response.StatusCode, contentType, Parse(text), null)
            : (response.StatusCode, contentType, [], JsonElement.Parse(text));
    }

    public static IReadOnlyList<JsonElement> Parse(string text)
    {
        var events = new List<JsonElement>();
        foreach (var block in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? id = null, name = null;
            var data = new StringBuilder();
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];
                else if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal)) data.Append(line[6..]);
            }

            if (data.Length == 0)
            {
                continue; // a comment such as a keepalive
            }

            var payload = JsonElement.Parse(data.ToString());
            ContractSchemas.AssertValid("stream-event", payload);
            Assert.Equal(payload.GetProperty("type").GetString(), name);
            Assert.Equal(payload.GetProperty("sequence").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture), id);
            Assert.Equal(events.Count + 1, payload.GetProperty("sequence").GetInt32());
            events.Add(payload);
        }

        return events;
    }

    public static IEnumerable<string> Types(IReadOnlyList<JsonElement> events) => events.Select(e => e.GetProperty("type").GetString()!);

    /// <summary>The reply text a client shows at the end: the deltas after the last reply.reset.</summary>
    public static string CountedText(IReadOnlyList<JsonElement> events)
    {
        var last = events.Select((e, i) => (e, i)).LastOrDefault(x => x.e.GetProperty("type").GetString() == "reply.reset").i;
        var hasReset = events.Any(e => e.GetProperty("type").GetString() == "reply.reset");
        return string.Concat(events.Skip(hasReset ? last + 1 : 0)
            .Where(e => e.GetProperty("type").GetString() == "reply.delta")
            .Select(e => e.GetProperty("data").GetProperty("text").GetString()));
    }

    public static JsonElement Final(IReadOnlyList<JsonElement> events)
    {
        Assert.Equal("turn.started", events[0].GetProperty("type").GetString());
        Assert.Single(events, e => e.GetProperty("type").GetString() == "turn.final");
        Assert.Equal("turn.final", events[^1].GetProperty("type").GetString());
        return events[^1].GetProperty("data").GetProperty("result");
    }
}
