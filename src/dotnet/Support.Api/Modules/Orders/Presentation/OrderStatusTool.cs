using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Presentation;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Orders.Application;
using Support.Api.Modules.Orders.Application.GetOrderStatus;
using Support.Api.Modules.Orders.Domain;

namespace Support.Api.Modules.Orders.Presentation;

/// <summary>
/// The only order capability the model gets: the model's "endpoint" into the Orders module. The model supplies an
/// order id and nothing else (no customer id, no confirmation). Ownership is decided by the query handler.
/// </summary>
public sealed class OrderStatusTool(IQueryHandler<GetOrderStatusQuery, Order> getOrderStatus)
{
    public const string Name = "get_order_status";

    public AIFunction AsAIFunction() => AIFunctionFactory.Create(GetOrderStatusAsync, Name);

    [Description("Look up the status of one of the current customer's orders. Use it whenever the customer asks about a specific order.")]
    public async Task<OrderStatusResult> GetOrderStatusAsync(
        [Description("The order id, in the form ORD-1234.")] string orderId,
        CancellationToken cancellationToken = default)
    {
        var result = await getOrderStatus.Handle(new GetOrderStatusQuery(orderId), cancellationToken).ConfigureAwait(false);
        return result.Match(
            order => Record(OrderLookupOutcome.Found, new OrderStatusResult(
                "found",
                order.OrderId,
                order.Status,
                order.PlacedAt,
                order.ShippedAt,
                order.DeliveredAt,
                [.. order.Items.Select(item => item.Name)])),
            error => error == OrderErrors.InvalidOrderId
                ? Record(OrderLookupOutcome.InvalidOrderId, new OrderStatusResult("invalid_order_id", null, null, null, null, null, null))
                : Record(OrderLookupOutcome.NotFound, new OrderStatusResult("not_found", OrderId.Normalize(orderId), null, null, null, null, null)));
    }

    private static OrderStatusResult Record(OrderLookupOutcome outcome, OrderStatusResult result)
    {
        TurnActivity.Current?.RecordOrderLookup(outcome);
        return result;
    }
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
