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
public sealed partial class FakeChatClient(Func<IReadOnlyList<ChatMessage>, ChatResponse>? script = null) : IChatClient
{
    /// <summary>
    /// The tool the default script asks for. A literal, not a reference to the Orders module, so the shared
    /// Agents module stays independent of feature modules; OrderStatusTests prove the names still match.
    /// </summary>
    public const string OrderStatusToolName = "get_order_status";

    private readonly Func<IReadOnlyList<ChatMessage>, ChatResponse> script = script ?? DefaultScript;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(script([.. messages]));
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
        var orderId = OrderIdPattern().Match(userText);
        if (orderId.Success)
        {
            return ToolCall(OrderStatusToolName, new Dictionary<string, object?> { ["orderId"] = orderId.Value.ToUpperInvariant() });
        }

        return Text("Which order do you mean? Please share the order id, for example ORD-1234.");
    }

    public static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    public static ChatResponse ToolCall(string name, IDictionary<string, object?> arguments) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call_{Guid.NewGuid():N}", name, arguments)]));

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
