using Support.Api.Modules.Billing.Domain;

namespace Support.Api.Modules.Billing.Application;

/// <summary>Where invoices and payments are read from. Today the synthetic fixtures; read-only by design.</summary>
public interface IBillingRepository
{
    Invoice? FindInvoice(string invoiceId);

    Invoice? FindInvoiceForOrder(string orderId);

    IReadOnlyList<Payment> PaymentsFor(string customerId);
}
