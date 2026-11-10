using Support.Api.Modules.Billing.Domain;

namespace Support.Api.Modules.Billing.Application;

/// <summary>Where invoices and payments are read from. Today the synthetic fixtures; read-only by design.</summary>
public interface IBillingRepository
{
    Invoice? FindInvoice(string invoiceId);

    Invoice? FindInvoiceForOrder(string orderId);

    IReadOnlyList<Payment> PaymentsFor(string customerId);

    /// <summary>Records a simulated refund (in memory only) and returns it with its new refund id.</summary>
    (Payment Payment, string RefundId) AddRefund(Invoice invoice, DateTimeOffset at);
}
