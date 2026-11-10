using System.Text.Json;
using System.Text.Json.Serialization;
using Support.Api.Modules.Billing.Application;
using Support.Api.Modules.Billing.Domain;

namespace Support.Api.Modules.Billing.Infrastructure;

/// <summary>Read-only invoices and payments from the synthetic fixtures shared with the Python backend.</summary>
internal sealed class FixtureBillingRepository : IBillingRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    private readonly IReadOnlyList<Invoice> invoices;
    private readonly IReadOnlyList<Payment> payments;

    private FixtureBillingRepository(IReadOnlyList<Invoice> invoices, IReadOnlyList<Payment> payments)
    {
        this.invoices = invoices;
        this.payments = payments;
    }

    public Invoice? FindInvoice(string invoiceId) =>
        invoices.FirstOrDefault(invoice => string.Equals(invoice.InvoiceId, invoiceId, StringComparison.Ordinal));

    public Invoice? FindInvoiceForOrder(string orderId) =>
        invoices.FirstOrDefault(invoice => string.Equals(invoice.OrderId, orderId, StringComparison.Ordinal));

    public IReadOnlyList<Payment> PaymentsFor(string customerId) =>
        [.. payments.Where(payment => string.Equals(payment.CustomerId, customerId, StringComparison.Ordinal))];

    /// <summary>Loads the fixture files copied next to the binaries (see Support.Api.csproj).</summary>
    public static FixtureBillingRepository Load(string? directory = null)
    {
        directory ??= Path.Combine(AppContext.BaseDirectory, "fixtures");
        return new FixtureBillingRepository(
            Read<InvoicesFile>(directory, "invoices.json").Invoices,
            Read<PaymentsFile>(directory, "payments.json").Payments);
    }

    private static T Read<T>(string directory, string file)
    {
        using var stream = File.OpenRead(Path.Combine(directory, file));
        return JsonSerializer.Deserialize<T>(stream, JsonOptions) ?? throw new InvalidOperationException($"fixtures/{file} is empty.");
    }

    private sealed record InvoicesFile(string FixtureVersion, IReadOnlyList<Invoice> Invoices);

    private sealed record PaymentsFile(string FixtureVersion, IReadOnlyList<Payment> Payments);
}
