using Microsoft.Extensions.AI;
using Support.Api.Common.Application.Messaging;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Presentation;
using Support.Api.Modules.Orders.Application.CancelOrder;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Orders.Application;
using Support.Api.Modules.Orders.Application.GetOrderStatus;
using Support.Api.Modules.Orders.Domain;
using Support.Api.Modules.Orders.Infrastructure;
using Support.Api.Modules.Orders.Presentation;

namespace Support.Api.Modules.Orders;

/// <summary>Wires the Orders module: order reads (with ownership checks), the model's order tool, and the orders agent.</summary>
internal static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services)
    {
        // Singletons: the agent (and so its tool) is built once, so everything the tool uses must live as long.
        services.AddSingleton<IOrderRepository>(_ => FixtureOrderRepository.Load());
        services.AddSingleton<IQueryHandler<GetOrderStatusQuery, Order>, GetOrderStatusQueryHandler>();
        services.AddSingleton<OrderStatusTool>();
        services.AddSingleton<IWriteHandler, CancelOrderWriteHandler>();
        services.AddSingleton(provider => new SupportAgent(
            SupportAgents.OrdersAgentName,
            OrdersAgentFactory.Create(
                provider.GetRequiredService<IChatClient>(),
                provider.GetRequiredService<OrderStatusTool>(),
                provider.GetRequiredService<OperationTools>(),
                provider.GetService<ILoggerFactory>())));
        return services;
    }
}
