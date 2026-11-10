using Support.Api.Common.Infrastructure.Persistence;
using Microsoft.Extensions.AI;
using Support.Api.Common.Application.Messaging;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Billing.Application;
using Support.Api.Modules.Billing.Application.GetInvoice;
using Support.Api.Modules.Billing.Application.GetPaymentHistory;
using Support.Api.Modules.Billing.Domain;
using Support.Api.Modules.Billing.Infrastructure;
using Support.Api.Modules.Billing.Application.RefundOrder;
using Support.Api.Modules.Billing.Presentation;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Presentation;

namespace Support.Api.Modules.Billing;

/// <summary>Wires the Billing module: customer-scoped invoice and payment reads, their tools, and the billing agent.</summary>
internal static class BillingModule
{
    public static IServiceCollection AddBillingModule(this IServiceCollection services)
    {
        services.AddSingleton<IBillingRepository>(provider => FixtureBillingRepository.Load(provider.GetRequiredService<SupportDatabase>()));
        services.AddSingleton<IQueryHandler<GetInvoiceQuery, Invoice>, GetInvoiceQueryHandler>();
        services.AddSingleton<IQueryHandler<GetPaymentHistoryQuery, IReadOnlyList<Payment>>, GetPaymentHistoryQueryHandler>();
        services.AddSingleton<BillingTools>();
        services.AddSingleton<IWriteHandler, RefundOrderWriteHandler>();
        services.AddSingleton(provider => new SupportAgent(
            SupportAgents.BillingAgentName,
            BillingAgentFactory.Create(provider.GetRequiredService<IChatClient>(), provider.GetRequiredService<BillingTools>(), provider.GetRequiredService<OperationTools>(), provider.GetService<ILoggerFactory>())));
        return services;
    }
}
