using Support.Api.Common.Domain;

namespace Support.Api.Modules.Billing.Domain;

/// <summary>An invoice for one order. Shapes match the shared synthetic fixtures (fixtures/data/invoices.json).</summary>
public sealed record Invoice(
    string InvoiceId,
    string OrderId,
    string CustomerId,
    DateTimeOffset IssuedAt,
    IReadOnlyList<InvoiceLine> Lines,
    Money Subtotal,
    Money Tax,
    Money Total,
    string PaymentMethod);

public sealed record InvoiceLine(string Description, int Quantity, Money Amount);

/// <summary>A charge or a refund (fixtures/data/payments.json). Read-only: nothing in this build creates one.</summary>
public sealed record Payment(
    string PaymentId,
    string CustomerId,
    string OrderId,
    string InvoiceId,
    DateTimeOffset PaidAt,
    Money Amount,
    string Method,
    string Kind);
