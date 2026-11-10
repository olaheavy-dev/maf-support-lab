using System.Text.Json;
using System.Text.Json.Serialization;
using Support.Api.Modules.Orders.Application;
using Support.Api.Modules.Orders.Domain;

namespace Support.Api.Modules.Orders.Infrastructure;

/// <summary>Orders from the synthetic fixtures shared with the Python backend, held in memory so simulated writes can change them.</summary>
internal sealed class FixtureOrderRepository : IOrderRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Order> orders;

    private FixtureOrderRepository(IReadOnlyList<Order> orders) =>
        this.orders = new(orders.ToDictionary(order => order.OrderId, StringComparer.Ordinal), StringComparer.Ordinal);

    public Order? Find(string orderId) => orders.GetValueOrDefault(orderId);

    public void Update(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        orders[order.OrderId] = order;
    }

    /// <summary>Loads the fixture files copied next to the binaries (see Support.Api.csproj).</summary>
    public static FixtureOrderRepository Load(string? directory = null)
    {
        directory ??= Path.Combine(AppContext.BaseDirectory, "fixtures");
        using var stream = File.OpenRead(Path.Combine(directory, "orders.json"));
        var file = JsonSerializer.Deserialize<OrdersFile>(stream, JsonOptions)
            ?? throw new InvalidOperationException("fixtures/orders.json is empty.");
        return new FixtureOrderRepository(file.Orders);
    }

    private sealed record OrdersFile(string FixtureVersion, IReadOnlyList<Order> Orders);
}
