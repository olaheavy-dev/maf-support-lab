using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Presentation;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Billing.Application;
using Support.Api.Modules.Billing.Application.GetInvoice;
using Support.Api.Modules.Billing.Application.GetPaymentHistory;
using Support.Api.Modules.Billing.Domain;

namespace Support.Api.Modules.Billing.Presentation;

/// <summary>
/// The billing agent's only capabilities: two reads. The model passes an invoice or order id, or nothing; never a
/// customer id. Ownership is decided by the query handlers. There is deliberately no refund or payment tool (L10).
/// </summary>
public sealed partial class BillingTools(
    IQueryHandler<GetInvoiceQuery, Invoice> getInvoice,
    IQueryHandler<GetPaymentHistoryQuery, IReadOnlyList<Payment>> getPaymentHistory,
    ToolMetrics metrics,
    ILogger<BillingTools> logger)
{
    public const string InvoiceToolName = "get_invoice";
    public const string PaymentHistoryToolName = "get_payment_history";

    public IReadOnlyList<AIFunction> AsAIFunctions() =>
    [
        AIFunctionFactory.Create(GetInvoiceAsync, InvoiceToolName),
        AIFunctionFactory.Create(GetPaymentHistoryAsync, PaymentHistoryToolName),
    ];

    [Description("Look up one of the current customer's invoices, by invoice id (INV-1234) or by order id (ORD-1234).")]
    public async Task<InvoiceResult> GetInvoiceAsync(
        [Description("The invoice id (INV-1234) or order id (ORD-1234).")] string reference,
        CancellationToken cancellationToken = default)
    {
        var result = await getInvoice.Handle(new GetInvoiceQuery(reference), cancellationToken).ConfigureAwait(false);
        return result.Match(
            invoice => Record(InvoiceToolName, ToolOutcome.Found, "found", InvoiceResult.From(invoice), invoice.OrderId),
            error => error == BillingErrors.InvalidReference
                ? Record(InvoiceToolName, ToolOutcome.InvalidInput, "invalid_reference", InvoiceResult.Missing("invalid_reference", null))
                : Record(InvoiceToolName, ToolOutcome.NotFound, "not_found", InvoiceResult.Missing("not_found", InvoiceReference.Normalize(reference))));
    }

    [Description("List the current customer's payments and refunds, newest first.")]
    public async Task<PaymentHistoryResult> GetPaymentHistoryAsync(CancellationToken cancellationToken = default)
    {
        var result = await getPaymentHistory.Handle(new GetPaymentHistoryQuery(), cancellationToken).ConfigureAwait(false);
        var payments = result.Match(found => found, _ => []);
        return Record(PaymentHistoryToolName, ToolOutcome.Found, "found", new PaymentHistoryResult([.. payments.Select(PaymentLine.From)]));
    }

    private T Record<T>(string tool, ToolOutcome outcome, string outcomeName, T result, string? verifiedOrderId = null)
    {
        TurnActivity.Current?.Record(outcome, verifiedOrderId);
        metrics.Record(tool, outcomeName);
        Log.LookupFinished(logger, tool, outcomeName);
        return result;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Tool {Tool} finished with outcome {Outcome}")]
        public static partial void LookupFinished(ILogger logger, string tool, string outcome);
    }
}

/// <summary>What the model sees for an invoice. Only the current customer's own data; amounts include a display form.</summary>
public sealed record InvoiceResult(
    [property: JsonPropertyName("lookup")] string Lookup,
    [property: JsonPropertyName("reference")] string? Reference,
    [property: JsonPropertyName("invoice_id")] string? InvoiceId,
    [property: JsonPropertyName("order_id")] string? OrderId,
    [property: JsonPropertyName("issued_at")] DateTimeOffset? IssuedAt,
    [property: JsonPropertyName("lines")] IReadOnlyList<string>? Lines,
    [property: JsonPropertyName("subtotal")] string? Subtotal,
    [property: JsonPropertyName("tax")] string? Tax,
    [property: JsonPropertyName("total")] string? Total,
    [property: JsonPropertyName("payment_method")] string? PaymentMethod)
{
    public static InvoiceResult From(Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return new(
            "found",
            invoice.InvoiceId,
            invoice.InvoiceId,
            invoice.OrderId,
            invoice.IssuedAt,
            [.. invoice.Lines.Select(line => $"{line.Quantity} x {line.Description}: {line.Amount.Display()}")],
            invoice.Subtotal.Display(),
            invoice.Tax.Display(),
            invoice.Total.Display(),
            invoice.PaymentMethod);
    }

    public static InvoiceResult Missing(string lookup, string? reference) =>
        new(lookup, reference, null, null, null, null, null, null, null, null);
}

public sealed record PaymentHistoryResult([property: JsonPropertyName("payments")] IReadOnlyList<PaymentLine> Payments);

public sealed record PaymentLine(
    [property: JsonPropertyName("payment_id")] string PaymentId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("order_id")] string OrderId,
    [property: JsonPropertyName("invoice_id")] string InvoiceId,
    [property: JsonPropertyName("paid_at")] DateTimeOffset PaidAt,
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("method")] string Method)
{
    public static PaymentLine From(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        return new(payment.PaymentId, payment.Kind, payment.OrderId, payment.InvoiceId, payment.PaidAt, payment.Amount.Display(), payment.Method);
    }
}
