using System.Text.Json;
using System.Text.Json.Serialization;

namespace Support.Api.Fixtures;

/// <summary>Read-only synthetic fixture data shared with the Python backend (fixtures/data).</summary>
public sealed class FixtureCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    private FixtureCatalog(IReadOnlyList<FixtureOrder> orders) => Orders = orders;

    public IReadOnlyList<FixtureOrder> Orders { get; }

    /// <summary>Loads the fixture files copied next to the binaries (see Support.Api.csproj).</summary>
    public static FixtureCatalog Load(string? directory = null)
    {
        directory ??= Path.Combine(AppContext.BaseDirectory, "fixtures");
        using var stream = File.OpenRead(Path.Combine(directory, "orders.json"));
        var file = JsonSerializer.Deserialize<OrdersFile>(stream, JsonOptions)
            ?? throw new InvalidOperationException("fixtures/orders.json is empty.");
        return new FixtureCatalog(file.Orders);
    }

    private sealed record OrdersFile(string FixtureVersion, IReadOnlyList<FixtureOrder> Orders);
}

public sealed record FixtureOrder(
    string OrderId,
    string CustomerId,
    string Status,
    DateTimeOffset PlacedAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? DeliveredAt,
    string RefundStatus,
    IReadOnlyList<FixtureOrderItem> Items,
    Money Total);

public sealed record FixtureOrderItem(string Sku, string Name, int Quantity, Money UnitPrice);

public sealed record Money(long AmountMinor, string Currency);
