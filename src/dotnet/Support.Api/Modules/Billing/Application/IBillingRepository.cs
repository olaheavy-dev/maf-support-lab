using Support.Api.Modules.Billing.Domain;

namespace Support.Api.Modules.Billing.Application;

/// <summary>Where invoices and payments are read from: the synthetic fixtures, plus simulated refunds stored durably (L11).</summary>
public interface IBillingRepository
{
    Invoice? FindInvoice(string invoiceId);

    Invoice? FindInvoiceForOrder(string orderId);

    IReadOnlyList<Payment> PaymentsFor(string customerId);

    /// <summary>Records a simulated refund (durably; inside an atomic unit of work it joins that transaction) and returns it with its new refund id.</summary>
    (Payment Payment, string RefundId) AddRefund(Invoice invoice, DateTimeOffset at);
}
