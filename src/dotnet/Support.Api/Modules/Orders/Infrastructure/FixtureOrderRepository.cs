using System.Text.Json;
using System.Text.Json.Serialization;
using Support.Api.Common.Infrastructure.Persistence;
using Support.Api.Modules.Orders.Application;
using Support.Api.Modules.Orders.Domain;

namespace Support.Api.Modules.Orders.Infrastructure;

/// <summary>
/// Orders from the synthetic fixtures shared with the Python backend. The fixture files are never changed: an order a
/// simulated write changed is stored in the local database (L11), and that stored copy wins over the fixture.
/// </summary>
internal sealed class FixtureOrderRepository : IOrderRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, Order> fixtures;
    private readonly SupportDatabase database;

    private FixtureOrderRepository(IReadOnlyList<Order> orders, SupportDatabase database)
    {
        fixtures = orders.ToDictionary(order => order.OrderId, StringComparer.Ordinal);
        this.database = database;
    }

    public Order? Find(string orderId)
    {
        var changed = database.Query("SELECT json FROM changed_orders WHERE order_id = @id", reader => JsonSerializer.Deserialize<Order>(reader.GetString(0), StoredJson), ("@id", orderId));
        return changed.Count == 1 ? changed[0] : fixtures.GetValueOrDefault(orderId);
    }

    public void Update(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        database.Execute(
            "INSERT INTO changed_orders(order_id, json) VALUES (@id, @json) ON CONFLICT(order_id) DO UPDATE SET json = excluded.json",
            ("@id", order.OrderId), ("@json", JsonSerializer.Serialize(order, StoredJson)));
    }

    /// <summary>Loads the fixture files copied next to the binaries (see Support.Api.csproj).</summary>
    public static FixtureOrderRepository Load(SupportDatabase database, string? directory = null)
    {
        directory ??= Path.Combine(AppContext.BaseDirectory, "fixtures");
        using var stream = File.OpenRead(Path.Combine(directory, "orders.json"));
        var file = JsonSerializer.Deserialize<OrdersFile>(stream, JsonOptions)
            ?? throw new InvalidOperationException("fixtures/orders.json is empty.");
        return new FixtureOrderRepository(file.Orders, database);
    }

    private sealed record OrdersFile(string FixtureVersion, IReadOnlyList<Order> Orders);
}
