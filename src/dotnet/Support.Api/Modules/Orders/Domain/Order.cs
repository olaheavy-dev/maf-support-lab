namespace Support.Api.Modules.Orders.Domain;

/// <summary>A customer's order. Shapes match the shared synthetic fixtures (fixtures/data/orders.json).</summary>
public sealed record Order(
    string OrderId,
    string CustomerId,
    string Status,
    DateTimeOffset PlacedAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? DeliveredAt,
    string RefundStatus,
    IReadOnlyList<OrderItem> Items,
    Money Total);

public sealed record OrderItem(string Sku, string Name, int Quantity, Money UnitPrice);

public sealed record Money(long AmountMinor, string Currency);
