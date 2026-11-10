using Support.Api.Common.Domain;

namespace Support.Api.Modules.Orders.Domain;

/// <summary>Eligibility rules from fixtures/data/policies.json. Each returns null when allowed, or the reason it is not.</summary>
public static class OrderRules
{
    public static string? WhyNotCancellable(Order order, SupportPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(policy);
        return policy.CancellableStatuses.Contains(order.Status, StringComparer.Ordinal)
            ? null
            : $"Order {order.OrderId} is {order.Status}; only orders that are still processing can be cancelled.";
    }

    /// <summary>Refunds need a delivered order, within the window counted from delivery to the fixtures' reference time, not refunded before.</summary>
    public static string? WhyNotRefundable(Order order, SupportPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(policy);
        if (!string.Equals(order.Status, policy.RefundRequiredStatus, StringComparison.Ordinal) || order.DeliveredAt is not { } delivered)
        {
            return $"Order {order.OrderId} is {order.Status}; only delivered orders can be refunded.";
        }

        if (!policy.RefundableRefundStatuses.Contains(order.RefundStatus, StringComparer.Ordinal))
        {
            return $"Order {order.OrderId} has already been refunded.";
        }

        return policy.ReferenceTime - delivered > TimeSpan.FromDays(policy.RefundWindowDays)
            ? $"Order {order.OrderId} was delivered more than {policy.RefundWindowDays} days ago, outside the refund window."
            : null;
    }
}
