using System.Text.Json;
using System.Text.Json.Serialization;
using Support.Api.Common.Infrastructure.Persistence;
using Support.Api.Modules.Billing.Application;
using Support.Api.Modules.Billing.Domain;

namespace Support.Api.Modules.Billing.Infrastructure;

/// <summary>
/// Invoices and payments from the synthetic fixtures shared with the Python backend (read-only), plus the refund
/// payments simulated writes recorded, which are stored in the local database with their id counters (L11).
/// </summary>
internal sealed class FixtureBillingRepository : IBillingRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyList<Invoice> invoices;
    private readonly IReadOnlyList<Payment> fixturePayments;
    private readonly SupportDatabase database;

    private FixtureBillingRepository(IReadOnlyList<Invoice> invoices, IReadOnlyList<Payment> payments, SupportDatabase database)
    {
        this.invoices = invoices;
        fixturePayments = payments;
        this.database = database;
    }

    public Invoice? FindInvoice(string invoiceId) =>
        invoices.FirstOrDefault(invoice => string.Equals(invoice.InvoiceId, invoiceId, StringComparison.Ordinal));

    public Invoice? FindInvoiceForOrder(string orderId) =>
        invoices.FirstOrDefault(invoice => string.Equals(invoice.OrderId, orderId, StringComparison.Ordinal));

    public IReadOnlyList<Payment> PaymentsFor(string customerId) =>
    [
        .. fixturePayments.Where(payment => string.Equals(payment.CustomerId, customerId, StringComparison.Ordinal)),
        .. database.Query(
            "SELECT json FROM payments WHERE customer_id = @customer ORDER BY payment_id",
            reader => JsonSerializer.Deserialize<Payment>(reader.GetString(0), StoredJson)!,
            ("@customer", customerId)),
    ];

    public (Payment Payment, string RefundId) AddRefund(Invoice invoice, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var refundId = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"RFD-{database.NextValue("refund", 1):0000}");
        var payment = new Payment(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"PAY-{database.NextValue("payment", 8001)}"),
            invoice.CustomerId, invoice.OrderId, invoice.InvoiceId, at, invoice.Total, invoice.PaymentMethod, "refund");
        database.Execute(
            "INSERT INTO payments(payment_id, customer_id, json) VALUES (@id, @customer, @json)",
            ("@id", payment.PaymentId), ("@customer", payment.CustomerId), ("@json", JsonSerializer.Serialize(payment, StoredJson)));
        return (payment, refundId);
    }

    /// <summary>Loads the fixture files copied next to the binaries (see Support.Api.csproj).</summary>
    public static FixtureBillingRepository Load(SupportDatabase database, string? directory = null)
    {
        directory ??= Path.Combine(AppContext.BaseDirectory, "fixtures");
        return new FixtureBillingRepository(
            Read<InvoicesFile>(directory, "invoices.json").Invoices,
            Read<PaymentsFile>(directory, "payments.json").Payments,
            database);
    }

    private static T Read<T>(string directory, string file)
    {
        using var stream = File.OpenRead(Path.Combine(directory, file));
        return JsonSerializer.Deserialize<T>(stream, JsonOptions) ?? throw new InvalidOperationException($"fixtures/{file} is empty.");
    }

    private sealed record InvoicesFile(string FixtureVersion, IReadOnlyList<Invoice> Invoices);

    private sealed record PaymentsFile(string FixtureVersion, IReadOnlyList<Payment> Payments);
}
