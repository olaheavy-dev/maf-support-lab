using Support.Api.Common.Application.Messaging;
using Support.Api.Modules.Orders.Domain;

namespace Support.Api.Modules.Orders.Application.GetOrderStatus;

/// <summary>Read one of the current customer's orders. There is deliberately no customer id here.</summary>
public sealed record GetOrderStatusQuery(string OrderId) : IQuery<Order>;
