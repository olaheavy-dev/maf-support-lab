using System.Diagnostics;
using System.Net;
using System.Text;

namespace Support.Api.Tests.Modules.Resilience;

/// <summary>
/// A fake of Anthropic's HTTP API under the real Anthropic SDK (L15). Each attempt gets the scripted response for its
/// number, so the SDK's real retries, backoff, and retry-after handling run deterministically, with no network.
/// Successful answers are a valid triage decision (for the triage call) or a short reply.
/// </summary>
internal sealed class FakeAnthropicHttp(Func<int, HttpResponseMessage?> fault) : HttpMessageHandler
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<TimeSpan> attempts = [];
    private readonly Lock gate = new();

    public Func<int, Task>? BeforeAttempt { get; init; }

    public int Attempts
    {
        get
        {
            lock (gate)
            {
                return attempts.Count;
            }
        }
    }

    public IReadOnlyList<TimeSpan> AttemptTimes
    {
        get
        {
            lock (gate)
            {
                return [.. attempts];
            }
        }
    }

    public static HttpResponseMessage Error(int status, string type, int? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"" + type + "\",\"message\":\"simulated\"}}", Encoding.UTF8, "application/json"),
        };
        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.TryAddWithoutValidation("retry-after", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int number;
        lock (gate)
        {
            attempts.Add(clock.Elapsed);
            number = attempts.Count;
        }

        if (BeforeAttempt is { } before)
        {
            await before(number).WaitAsync(cancellationToken);
        }

        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        // The triage call asks for structured output (output_config with a JSON schema); the specialist does not.
        return fault(number) ?? Answer(body.Contains("\"output_config\"", StringComparison.Ordinal));
    }

    private static HttpResponseMessage Answer(bool triage)
    {
        var text = triage
            ? "{\\\"target\\\":\\\"orders\\\",\\\"requested_action\\\":\\\"order_status\\\",\\\"order_id\\\":\\\"ORD-1002\\\",\\\"summary\\\":\\\"Order status.\\\",\\\"customer_sentiment\\\":\\\"neutral\\\",\\\"confidence\\\":0.9}"
            : "Your order ORD-1002 has shipped.";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-haiku-5-5\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}],\"stop_reason\":\"end_turn\",\"stop_sequence\":null,\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}",
                Encoding.UTF8,
                "application/json"),
        };
    }
}
