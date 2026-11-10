using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Support.Api.Agents;

namespace Support.Api.Orders;

/// <summary>
/// The only order capability the model gets. The model supplies an order id and nothing else:
/// no customer id, no confirmation. Ownership is checked by <see cref="OrderService"/>.
/// </summary>
public sealed partial class OrderStatusTool(OrderService orders)
{
    public const string Name = "get_order_status";

    public AIFunction AsAIFunction() => AIFunctionFactory.Create(GetOrderStatus, Name);

    [Description("Look up the status of one of the current customer's orders. Use it whenever the customer asks about a specific order.")]
    public OrderStatusResult GetOrderStatus(
        [Description("The order id, in the form ORD-1234.")] string orderId)
    {
        var normalized = orderId?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!OrderIdPattern().IsMatch(normalized))
        {
            return Record(OrderLookupOutcome.InvalidOrderId, new OrderStatusResult("invalid_order_id", null, null, null, null, null, null));
        }

        var order = orders.FindForCurrentCustomer(normalized);
        if (order is null)
        {
            return Record(OrderLookupOutcome.NotFound, new OrderStatusResult("not_found", normalized, null, null, null, null, null));
        }

        return Record(OrderLookupOutcome.Found, new OrderStatusResult(
            "found",
            order.OrderId,
            order.Status,
            order.PlacedAt,
            order.ShippedAt,
            order.DeliveredAt,
            [.. order.Items.Select(item => item.Name)]));
    }

    private static OrderStatusResult Record(OrderLookupOutcome outcome, OrderStatusResult result)
    {
        TurnActivity.Current?.RecordOrderLookup(outcome);
        return result;
    }

    [GeneratedRegex("^ORD-[0-9]{4}$")]
    private static partial Regex OrderIdPattern();
}

/// <summary>What the model sees. Contains only the current customer's own data.</summary>
public sealed record OrderStatusResult(
    [property: JsonPropertyName("lookup")] string Lookup,
    [property: JsonPropertyName("order_id")] string? OrderId,
    [property: JsonPropertyName("order_status")] string? OrderStatus,
    [property: JsonPropertyName("placed_at")] DateTimeOffset? PlacedAt,
    [property: JsonPropertyName("shipped_at")] DateTimeOffset? ShippedAt,
    [property: JsonPropertyName("delivered_at")] DateTimeOffset? DeliveredAt,
    [property: JsonPropertyName("items")] IReadOnlyList<string>? Items);
