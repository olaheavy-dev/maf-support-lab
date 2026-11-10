using System.Text.RegularExpressions;

namespace Support.Api.Modules.Orders.Domain;

/// <summary>Order ids look like ORD-1234. Model and client input is normalized before it is checked.</summary>
public static partial class OrderId
{
    public static string Normalize(string? orderId) => orderId?.Trim().ToUpperInvariant() ?? string.Empty;

    public static bool IsValid(string normalizedOrderId) => Pattern().IsMatch(normalizedOrderId);

    [GeneratedRegex("^ORD-[0-9]{4}$")]
    private static partial Regex Pattern();
}
