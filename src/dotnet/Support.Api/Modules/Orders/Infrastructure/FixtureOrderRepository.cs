using System.Text.Json;
using System.Text.Json.Serialization;
using Support.Api.Modules.Orders.Application;
using Support.Api.Modules.Orders.Domain;

namespace Support.Api.Modules.Orders.Infrastructure;

/// <summary>Read-only orders from the synthetic fixtures shared with the Python backend (fixtures/data).</summary>
internal sealed class FixtureOrderRepository : IOrderRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    private readonly IReadOnlyList<Order> orders;

    private FixtureOrderRepository(IReadOnlyList<Order> orders) => this.orders = orders;

    public Order? Find(string orderId) =>
        orders.FirstOrDefault(order => string.Equals(order.OrderId, orderId, StringComparison.Ordinal));

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
