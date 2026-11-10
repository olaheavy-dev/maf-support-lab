using System.Text.RegularExpressions;

namespace Support.Api.Modules.Billing.Domain;

/// <summary>How a customer refers to an invoice: by its own id (INV-5001) or by its order (ORD-1001).</summary>
public static partial class InvoiceReference
{
    public static string Normalize(string? reference) => reference?.Trim().ToUpperInvariant() ?? string.Empty;

    public static bool IsInvoiceId(string normalized) => InvoicePattern().IsMatch(normalized);

    public static bool IsOrderId(string normalized) => OrderPattern().IsMatch(normalized);

    [GeneratedRegex("^INV-[0-9]{4}$")]
    private static partial Regex InvoicePattern();

    [GeneratedRegex("^ORD-[0-9]{4}$")]
    private static partial Regex OrderPattern();
}
