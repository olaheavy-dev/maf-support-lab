using System.Text.Json.Nodes;
using Support.Api.Common.Domain;
using Support.Api.Modules.Identity.Application;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;
using Support.Api.Modules.Orders.Domain;

namespace Support.Api.Modules.Orders.Application.CancelOrder;

/// <summary>Simulated cancellation: only the customer's own order, and only while it is still processing.</summary>
internal sealed class CancelOrderWriteHandler(IOrderRepository orders, IPrincipalSource principals, SupportPolicy policy) : IWriteHandler
{
    private readonly Lock gate = new();

    public OperationKind Kind => OperationKind.CancelOrder;

    public Task<Result<string>> CheckAsync(OperationArguments arguments, CancellationToken cancellationToken)
    {
        var order = Owned(arguments);
        if (order is null)
        {
            return Task.FromResult(Result.Failure<string>(WriteErrors.NotFound));
        }

        return Task.FromResult(OrderRules.WhyNotCancellable(order, policy) is { } reason
            ? Result.Failure<string>(WriteErrors.NotEligible(reason))
            : Result.Success($"Cancel order {order.OrderId} ({string.Join(", ", order.Items.Select(item => item.Name))})."));
    }

    public Task<Result<JsonObject>> ExecuteAsync(OperationArguments arguments, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var order = Owned(arguments);
            if (order is null)
            {
                return Task.FromResult(Result.Failure<JsonObject>(WriteErrors.NotFound));
            }

            if (OrderRules.WhyNotCancellable(order, policy) is { } reason)
            {
                return Task.FromResult(Result.Failure<JsonObject>(WriteErrors.NotEligible(reason)));
            }

            orders.Update(order with { Status = "cancelled" });
            return Task.FromResult(Result.Success(new JsonObject { ["order_id"] = order.OrderId, ["order_status"] = "cancelled" }));
        }
    }

    private Order? Owned(OperationArguments arguments) =>
        arguments["order_id"] is { } id && orders.Find(id) is { } order && order.CustomerId == principals.Current.CustomerId ? order : null;
}
