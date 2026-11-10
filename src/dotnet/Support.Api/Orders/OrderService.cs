using Support.Api.Fixtures;
using Support.Api.Identity;

namespace Support.Api.Orders;

/// <summary>
/// Order reads with ownership enforced in code. The caller's identity always comes from
/// <see cref="IPrincipalSource"/>; nothing the model or client sends can change it.
/// </summary>
public sealed class OrderService(FixtureCatalog fixtures, IPrincipalSource principals)
{
    /// <summary>
    /// Returns the order only if it belongs to the current customer. An order owned by someone else
    /// returns null, exactly like a missing one, so callers can't learn that it exists.
    /// </summary>
    public FixtureOrder? FindForCurrentCustomer(string orderId)
    {
        var customerId = principals.Current.CustomerId;
        return fixtures.Orders.FirstOrDefault(order =>
            string.Equals(order.OrderId, orderId, StringComparison.Ordinal) &&
            string.Equals(order.CustomerId, customerId, StringComparison.Ordinal));
    }
}
