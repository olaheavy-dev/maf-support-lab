using System.Text.Json.Nodes;
using Support.Api.Common.Domain;
using Support.Api.Modules.Identity.Application;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;
using Support.Api.Modules.Orders.Application;
using Support.Api.Modules.Orders.Domain;

namespace Support.Api.Modules.Billing.Application.RefundOrder;

/// <summary>
/// Simulated full refund of the customer's own delivered order, within the refund window, once. Records a refund
/// payment and marks the order refunded, in memory only.
/// </summary>
internal sealed class RefundOrderWriteHandler(
    IOrderRepository orders, IBillingRepository billing, IPrincipalSource principals, SupportPolicy policy, TimeProvider clock) : IWriteHandler
{
    private readonly Lock gate = new();

    public OperationKind Kind => OperationKind.RefundOrder;

    public Task<Result<string>> CheckAsync(OperationArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var (order, invoice) = Owned(arguments);
        if (order is null || invoice is null)
        {
            return Task.FromResult(Result.Failure<string>(WriteErrors.NotFound));
        }

        return Task.FromResult(OrderRules.WhyNotRefundable(order, policy) is { } reason
            ? Result.Failure<string>(WriteErrors.NotEligible(reason))
            : Result.Success($"Refund order {order.OrderId} in full ({invoice.Total.Display()}) for reason: {arguments["reason"]}"));
    }

    public Task<Result<JsonObject>> ExecuteAsync(OperationArguments arguments, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var (order, invoice) = Owned(arguments);
            if (order is null || invoice is null)
            {
                return Task.FromResult(Result.Failure<JsonObject>(WriteErrors.NotFound));
            }

            if (OrderRules.WhyNotRefundable(order, policy) is { } reason)
            {
                return Task.FromResult(Result.Failure<JsonObject>(WriteErrors.NotEligible(reason)));
            }

            var (_, refundId) = billing.AddRefund(invoice, clock.GetUtcNow());
            orders.Update(order with { RefundStatus = "refunded" });
            return Task.FromResult(Result.Success(new JsonObject
            {
                ["refund_id"] = refundId,
                ["order_id"] = order.OrderId,
                ["amount"] = new JsonObject { ["amount_minor"] = invoice.Total.AmountMinor, ["currency"] = invoice.Total.Currency },
            }));
        }
    }

    private (Order? Order, Domain.Invoice? Invoice) Owned(OperationArguments arguments) =>
        arguments["order_id"] is { } id && orders.Find(id) is { } order && order.CustomerId == principals.Current.CustomerId
            ? (order, billing.FindInvoiceForOrder(id))
            : (null, null);
}
