using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Domain;
using Support.Api.Modules.Identity.Application;
using Support.Api.Modules.Orders.Domain;

namespace Support.Api.Modules.Orders.Application.GetOrderStatus;

/// <summary>
/// Order reads with ownership enforced in code. The caller's identity always comes from
/// <see cref="IPrincipalSource"/>; nothing the model or client sends can change it.
/// </summary>
internal sealed class GetOrderStatusQueryHandler(IOrderRepository orders, IPrincipalSource principals)
    : IQueryHandler<GetOrderStatusQuery, Order>
{
    public Task<Result<Order>> Handle(GetOrderStatusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var orderId = OrderId.Normalize(query.OrderId);
        if (!OrderId.IsValid(orderId))
        {
            return Task.FromResult(Result.Failure<Order>(OrderErrors.InvalidOrderId));
        }

        // Another customer's order is reported exactly like a missing one.
        var order = orders.Find(orderId);
        var owned = order is not null && string.Equals(order.CustomerId, principals.Current.CustomerId, StringComparison.Ordinal);
        return Task.FromResult(owned ? Result.Success(order!) : Result.Failure<Order>(OrderErrors.NotFound));
    }
}
