using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Support.Api.Modules.Agents.Application;

namespace Support.Api.Modules.Agents.Infrastructure.ModelProvider;

/// <summary>
/// Deterministic stand-in for the model provider: no network, no credentials, no cost.
/// Triage calls (JSON response format) get keyword triage. Specialist calls get a script that behaves like a
/// well-behaved model: it calls whichever of the agent's own tools fits, then answers from the tool result.
/// Tests can pass their own scripts, for example one that requests another customer's order.
/// </summary>
public sealed partial class FakeChatClient : IChatClient
{
    /// <summary>
    /// Tool names the default script uses. Literals, not references to feature modules, so the shared Agents module
    /// stays independent of them; tests prove the names still match the real tools.
    /// </summary>
    public const string OrderStatusToolName = "get_order_status";
    public const string InvoiceToolName = "get_invoice";
    public const string PaymentHistoryToolName = "get_payment_history";
    public const string KnowledgeSearchToolName = "search_knowledge_base";
    public const string CancelOrderToolName = "cancel_order";
    public const string RefundOrderToolName = "refund_order";
    public const string CreateTicketToolName = "create_ticket";

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

    /// <summary>
    /// Messages containing this marker keep the specialist's model call busy (cancellably), so a running turn can be
    /// cancelled from outside, as the evaluation does. Fake mode only.
    /// </summary>
    public const string SlowTrigger = "[fake:slow-model]";

    /// <summary>How long a slow specialist call takes: far longer than a cancel needs, far shorter than the turn deadline.</summary>
    public static readonly TimeSpan SlowDelay = TimeSpan.FromSeconds(8);

    private readonly Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> script;
    private readonly Func<string, string> triage;

    /// <summary>A fake whose specialist calls see the agent's options (and so its tools).</summary>
    public FakeChatClient(Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse>? script = null, Func<string, string>? triage = null)
    {
        this.script = script ?? DefaultScript;
        this.triage = triage ?? DefaultTriage;
    }

    /// <summary>A fake with a messages-only specialist script (tests that script one behaviour).</summary>
    public FakeChatClient(Func<IReadOnlyList<ChatMessage>, ChatResponse> script, Func<string, string>? triage = null)
        : this((messages, _) => script(messages), triage)
    {
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ChatMessage> history = [.. messages];

        // Only the triage agent asks for JSON output, so a JSON response format means "this is the triage call".
        // Scripted tests therefore control the specialist model while triage stays deterministic.
        if (options?.ResponseFormat is ChatResponseFormatJson)
        {
            return Task.FromResult(Text(triage(LastUserText(history))));
        }

        return Task.FromResult(script(history, options));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessage> history = [.. messages];
        if (options?.ResponseFormat is not ChatResponseFormatJson && LastUserText(history).Contains(SlowTrigger, StringComparison.Ordinal))
        {
            await Task.Delay(SlowDelay, cancellationToken).ConfigureAwait(false);
        }

        // Text comes out word by word, like a real model's tokens, so streaming has several deltas to show.
        var response = await GetResponseAsync(history, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            if (update.Contents is [TextContent { Text: { Length: > 0 } text }])
            {
                foreach (var word in System.Text.RegularExpressions.Regex.Split(text, @"(?<=\s)").Where(word => word.Length > 0))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new ChatResponseUpdate(update.Role, word) { MessageId = update.MessageId, ResponseId = update.ResponseId, ModelId = update.ModelId };
                }

                continue;
            }

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

    /// <summary>The orders behaviour, for scripts that wrap it without knowing the agent's tools.</summary>
    public static ChatResponse DefaultScript(IReadOnlyList<ChatMessage> messages) => DefaultScript(messages, null);

    /// <summary>
    /// Calls the fitting tool among those the agent actually has, then answers from its result. With no tool
    /// information it behaves like the orders model.
    /// </summary>
    public static ChatResponse DefaultScript(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var toolResult = messages.Count > 0 ? messages[^1].Contents.OfType<FunctionResultContent>().LastOrDefault() : null;
        if (toolResult is not null)
        {
            return Text(Describe(ToolNameFor(messages, toolResult.CallId), toolResult.Result));
        }

        var userText = LastUserText(messages);
        if (userText.Contains(FailureTrigger, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The fake model failed because the message asked it to.");
        }

        var tools = options?.Tools?.OfType<AIFunctionDeclaration>().Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal)
            ?? [OrderStatusToolName];
        // Like a real model, use the trusted context note when the customer refers to "it" without an id.
        var contextText = string.Join(" ", messages.Where(ConversationHistory.IsContextNote).Select(message => message.Text));
        var orderId = OrderIdPattern().Match(userText) is { Success: true } named ? named : OrderIdPattern().Match(contextText);
        var invoiceId = InvoiceIdPattern().Match(userText);
        var upper = userText.ToUpperInvariant();

        // Write requests first, like a model that does what was asked: the framework then pauses for approval.
        if (tools.Contains(CancelOrderToolName) && upper.Contains("CANCEL", StringComparison.Ordinal) && orderId.Success)
        {
            return ToolCall(CancelOrderToolName, new Dictionary<string, object?> { ["orderId"] = orderId.Value.ToUpperInvariant() });
        }

        if (tools.Contains(RefundOrderToolName) && upper.Contains("REFUND", StringComparison.Ordinal) && orderId.Success)
        {
            return ToolCall(RefundOrderToolName, new Dictionary<string, object?> { ["orderId"] = orderId.Value.ToUpperInvariant(), ["reason"] = userText });
        }

        if (tools.Contains(CreateTicketToolName) && upper.Contains("TICKET", StringComparison.Ordinal))
        {
            return ToolCall(CreateTicketToolName, new Dictionary<string, object?> { ["subject"] = "Support request", ["description"] = userText, ["priority"] = "normal" });
        }

        if (tools.Contains(KnowledgeSearchToolName))
        {
            return ToolCall(KnowledgeSearchToolName, new Dictionary<string, object?> { ["query"] = userText });
        }

        if (tools.Contains(PaymentHistoryToolName) && ContainsAny(upper, "PAYMENT", "HISTORY", "CHARGE", "PAID"))
        {
            return ToolCall(PaymentHistoryToolName, new Dictionary<string, object?>());
        }

        if (tools.Contains(InvoiceToolName))
        {
            var reference = invoiceId.Success ? invoiceId.Value : orderId.Success ? orderId.Value : null;
            return reference is null
                ? Text("Which invoice do you mean? Please share the order id (ORD-1234) or invoice id (INV-1234).")
                : ToolCall(InvoiceToolName, new Dictionary<string, object?> { ["reference"] = reference.ToUpperInvariant() });
        }

        if (tools.Contains(OrderStatusToolName) && orderId.Success)
        {
            return ToolCall(OrderStatusToolName, new Dictionary<string, object?> { ["orderId"] = orderId.Value.ToUpperInvariant() });
        }

        return Text("Which order do you mean? Please share the order id, for example ORD-1234.");
    }

    /// <summary>
    /// Keyword triage that behaves like a well-behaved classifier: it returns the contract's triage-decision JSON.
    /// Billing words mean billing; order words or an order id mean orders; product-problem words mean technical;
    /// anything else is "other", which the routing rules treat as ambiguous.
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
            : ContainsAny(text, "TICKET") ? ("technical", "create_ticket")
            : orderId.Success || ContainsAny(text, "ORDER", "DELIVER", "PACKAGE", "SHIPP", "TRACK") ? ("orders", "order_status")
            : ContainsAny(text, "ERROR", "CRASH", "NOT WORKING", "BROKEN", "LOGIN", "LOG IN", "RESET", "WON'T", "CRACKL", "WIFI", "WI-FI",
                "FIRMWARE", "AUDIO", "BLUETOOTH", "WOBBL", "SETUP", "CONNECT", "STATIC") ? ("technical", "troubleshooting")
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

    private static string LastUserText(IReadOnlyList<ChatMessage> messages) =>
        messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;

    private static bool ContainsAny(string text, params string[] words) =>
        words.Any(word => text.Contains(word, StringComparison.Ordinal));

    /// <summary>The tool a result belongs to: the earlier function call with the same call id.</summary>
    private static string? ToolNameFor(IReadOnlyList<ChatMessage> messages, string callId) =>
        messages.SelectMany(message => message.Contents.OfType<FunctionCallContent>())
            .LastOrDefault(call => string.Equals(call.CallId, callId, StringComparison.Ordinal))?.Name;

    private static string Describe(string? tool, object? result)
    {
        // A native rejection reaches the model as text: "... rejected by the approver and must not be retried. <reason>".
        if (result is string text || (result is JsonElement { ValueKind: JsonValueKind.String } textElement && (text = textElement.GetString()!) is not null))
        {
            return text.Contains("rejected", StringComparison.OrdinalIgnoreCase)
                ? $"Understood, I won't go ahead with that. {text[(text.IndexOf("retried.", StringComparison.Ordinal) is var at and >= 0 ? at + 8 : 0)..].Trim()}".Trim()
                : text;
        }

        var json = result is JsonElement element ? element : JsonSerializer.SerializeToElement(result);
        string? Field(string name) => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        return tool switch
        {
            InvoiceToolName => Field("lookup") switch
            {
                "found" => $"Invoice {Field("invoice_id")} for order {Field("order_id")}: total {Field("total")}, paid with {Field("payment_method")}.",
                "not_found" => $"I couldn't find {Field("reference")} on your account. Please check the id.",
                _ => "Please share the order id (ORD-1234) or invoice id (INV-1234).",
            },
            PaymentHistoryToolName => "Your payments: " + string.Join("; ", json.GetProperty("payments").EnumerateArray().Select(payment =>
                $"{payment.GetProperty("payment_id").GetString()} {payment.GetProperty("kind").GetString()} of {payment.GetProperty("amount").GetString()} for {payment.GetProperty("order_id").GetString()}")) + ".",
            CancelOrderToolName or RefundOrderToolName or CreateTicketToolName => Field("status") switch
            {
                "committed" when tool == CancelOrderToolName => $"Done: order {json.GetProperty("result").GetProperty("order_id").GetString()} is cancelled.",
                "committed" when tool == RefundOrderToolName => $"Done: refund {json.GetProperty("result").GetProperty("refund_id").GetString()} for order {json.GetProperty("result").GetProperty("order_id").GetString()} has been recorded.",
                "committed" => $"Done: support ticket {json.GetProperty("result").GetProperty("ticket_id").GetString()} is open.",
                _ => $"I couldn't do that: {Field("detail")}",
            },
            KnowledgeSearchToolName => json.GetProperty("articles").EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } article
                ? $"From {article.GetProperty("article_id").GetString()} ({article.GetProperty("title").GetString()}): " + string.Join(" ", article.GetProperty("steps").EnumerateArray().Select(step => step.GetString()))
                : "I couldn't find a documented fix for that. I can pass this to a human support agent.",
            _ => Field("lookup") switch
            {
                "found" => $"Order {Field("order_id")} is {Field("order_status")}.",
                "not_found" => $"I couldn't find order {Field("order_id")} on your account. Please check the order id.",
                _ => "That doesn't look like an order id. Please share it in the form ORD-1234.",
            },
        };
    }

    [GeneratedRegex("ORD-[0-9]{4}", RegexOptions.IgnoreCase)]
    private static partial Regex OrderIdPattern();

    [GeneratedRegex("INV-[0-9]{4}", RegexOptions.IgnoreCase)]
    private static partial Regex InvoiceIdPattern();
}
