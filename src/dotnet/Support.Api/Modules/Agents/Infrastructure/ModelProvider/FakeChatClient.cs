using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Support.Api.Modules.Agents.Infrastructure.ModelProvider;

/// <summary>
/// Deterministic stand-in for the model provider: no network, no credentials, no cost.
/// The default script behaves like a well-behaved orders model. Tests can pass their own script,
/// for example one that requests another customer's order, to prove the services still decide.
/// </summary>
public sealed partial class FakeChatClient(
    Func<IReadOnlyList<ChatMessage>, ChatResponse>? script = null,
    Func<string, string>? triage = null) : IChatClient
{
    /// <summary>
    /// The tool the default script asks for. A literal, not a reference to the Orders module, so the shared
    /// Agents module stays independent of feature modules; OrderStatusTests prove the names still match.
    /// </summary>
    public const string OrderStatusToolName = "get_order_status";

    /// <summary>
    /// A message containing this marker makes the fake model fail, so a failed turn can be produced and inspected in
    /// Aspire without a real provider outage. Fake mode only; Live mode never sees this code.
    /// </summary>
    public const string FailureTrigger = "[fake:model-error]";

    /// <summary>Fixed token counts on every fake response, so token-usage metrics have real samples in fake mode.</summary>
    public const int FakeInputTokens = 100;

    public const int FakeOutputTokens = 20;

    /// <summary>Messages containing this marker get triage output that is not JSON (fake mode only).</summary>
    public const string MalformedTriageTrigger = "[fake:triage-malformed]";

    /// <summary>Messages containing this marker get triage output naming a target the contract does not allow (fake mode only).</summary>
    public const string UnknownRouteTrigger = "[fake:triage-unknown-route]";

    private readonly Func<IReadOnlyList<ChatMessage>, ChatResponse> script = script ?? DefaultScript;
    private readonly Func<string, string> triage = triage ?? DefaultTriage;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ChatMessage> history = [.. messages];

        // Only the triage agent asks for JSON output, so a JSON response format means "this is the triage call".
        // Scripted tests therefore control the specialist model while triage stays deterministic.
        if (options?.ResponseFormat is ChatResponseFormatJson)
        {
            var userText = history.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;
            return Task.FromResult(Text(triage(userText)));
        }

        return Task.FromResult(script(history));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this
        : serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("fake", defaultModelId: "fake-orders-model")
        : null;

    public void Dispose()
    {
    }

    /// <summary>Asks for the tool when an order id is present, answers from the tool result, otherwise asks for the id.</summary>
    public static ChatResponse DefaultScript(IReadOnlyList<ChatMessage> messages)
    {
        var toolResult = messages.Count > 0 ? messages[^1].Contents.OfType<FunctionResultContent>().LastOrDefault() : null;
        if (toolResult is not null)
        {
            return Text(DescribeLookup(toolResult.Result));
        }

        var userText = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;
        if (userText.Contains(FailureTrigger, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The fake model failed because the message asked it to.");
        }

        var orderId = OrderIdPattern().Match(userText);
        if (orderId.Success)
        {
            return ToolCall(OrderStatusToolName, new Dictionary<string, object?> { ["orderId"] = orderId.Value.ToUpperInvariant() });
        }

        return Text("Which order do you mean? Please share the order id, for example ORD-1234.");
    }

    /// <summary>
    /// Keyword triage that behaves like a well-behaved classifier: it returns the contract's triage-decision JSON.
    /// An order id or order words mean orders; invoice, payment, or refund words mean billing; problem words mean
    /// technical; anything else is "other", which the routing rules treat as ambiguous.
    /// </summary>
    public static string DefaultTriage(string userText)
    {
        ArgumentNullException.ThrowIfNull(userText);
        if (userText.Contains(MalformedTriageTrigger, StringComparison.Ordinal))
        {
            return "Sure! This looks like an order question.";
        }

        var orderId = OrderIdPattern().Match(userText);
        var text = userText.ToUpperInvariant();
        var (target, action) =
            userText.Contains(UnknownRouteTrigger, StringComparison.Ordinal) ? ("shipping", "order_status")
            : ContainsAny(text, "REFUND") ? ("billing", "refund_order")
            : ContainsAny(text, "INVOICE", "BILL", "RECEIPT") ? ("billing", "invoice")
            : ContainsAny(text, "PAYMENT", "CHARGE", "PAID") ? ("billing", "payment_history")
            : ContainsAny(text, "CANCEL") ? ("orders", "cancel_order")
            : orderId.Success || ContainsAny(text, "ORDER", "DELIVER", "PACKAGE", "SHIPP", "TRACK") ? ("orders", "order_status")
            : ContainsAny(text, "ERROR", "CRASH", "NOT WORKING", "BROKEN", "LOGIN", "LOG IN", "RESET", "WON'T") ? ("technical", "troubleshooting")
            : ("orders", "other");

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["target"] = target,
            ["requested_action"] = action,
            ["order_id"] = orderId.Success ? orderId.Value.ToUpperInvariant() : null,
            ["summary"] = "Customer support request.",
            ["customer_sentiment"] = ContainsAny(text, "ANGRY", "FRUSTRAT", "!!", "RIDICULOUS") ? "frustrated" : "neutral",
            ["confidence"] = action == "other" ? 0.4 : 0.9,
        });
    }

    private static bool ContainsAny(string text, params string[] words) =>
        words.Any(word => text.Contains(word, StringComparison.Ordinal));

    public static ChatResponse Text(string text) =>
        WithUsage(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

    public static ChatResponse ToolCall(string name, IDictionary<string, object?> arguments) =>
        WithUsage(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call_{Guid.NewGuid():N}", name, arguments)])));

    private static ChatResponse WithUsage(ChatResponse response)
    {
        response.ModelId = "fake-orders-model";
        response.Usage = new UsageDetails
        {
            InputTokenCount = FakeInputTokens,
            OutputTokenCount = FakeOutputTokens,
            TotalTokenCount = FakeInputTokens + FakeOutputTokens,
        };
        return response;
    }

    private static string DescribeLookup(object? result)
    {
        var json = result is JsonElement element ? element : JsonSerializer.SerializeToElement(result);
        var lookup = json.TryGetProperty("lookup", out var value) ? value.GetString() : null;
        var orderId = json.TryGetProperty("order_id", out var id) ? id.GetString() : null;
        return lookup switch
        {
            "found" => $"Order {orderId} is {json.GetProperty("order_status").GetString()}.",
            "not_found" => $"I couldn't find order {orderId} on your account. Please check the order id.",
            _ => "That doesn't look like an order id. Please share it in the form ORD-1234.",
        };
    }

    [GeneratedRegex("ORD-[0-9]{4}", RegexOptions.IgnoreCase)]
    private static partial Regex OrderIdPattern();
}
