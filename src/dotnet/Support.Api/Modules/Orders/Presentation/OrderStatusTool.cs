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
public sealed partial class OrderStatusTool(
    IQueryHandler<GetOrderStatusQuery, Order> getOrderStatus,
    ToolMetrics metrics,
    ILogger<OrderStatusTool> logger)
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
            order => Record(ToolOutcome.Found, "found", new OrderStatusResult(
                "found",
                order.OrderId,
                order.Status,
                order.PlacedAt,
                order.ShippedAt,
                order.DeliveredAt,
                [.. order.Items.Select(item => item.Name)])),
            error => error == OrderErrors.InvalidOrderId
                ? Record(ToolOutcome.InvalidInput, "invalid_order_id", new OrderStatusResult("invalid_order_id", null, null, null, null, null, null))
                : Record(ToolOutcome.NotFound, "not_found", new OrderStatusResult("not_found", OrderId.Normalize(orderId), null, null, null, null, null)));
    }

    /// <summary>
    /// Records the outcome three ways: for the turn's reply kind (TurnActivity), as a bounded metric, and as a log line.
    /// The order id is not logged: it is model-controlled input, and the execute_tool span already shows the call.
    /// </summary>
    private OrderStatusResult Record(ToolOutcome outcome, string outcomeName, OrderStatusResult result)
    {
        TurnActivity.Current?.Record(outcome);
        metrics.Record(Name, outcomeName);
        Log.LookupFinished(logger, Name, outcomeName);
        return result;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Tool {Tool} finished with outcome {Outcome}")]
        public static partial void LookupFinished(ILogger logger, string tool, string outcome);
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
