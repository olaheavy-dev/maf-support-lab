using Support.Api.Common.Domain;

namespace Support.Api.Modules.Orders.Application;

public static class OrderErrors
{
    public static readonly Error InvalidOrderId = Error.Validation("invalid_order_id", "The order id must look like ORD-1234.");

    /// <summary>Used for missing orders and for other customers' orders alike, so callers can't learn that an order exists.</summary>
    public static readonly Error NotFound = Error.NotFound("not_found", "The order does not exist or is not available to you.");
}
