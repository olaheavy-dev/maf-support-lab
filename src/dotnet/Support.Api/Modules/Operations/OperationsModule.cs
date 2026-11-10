using Support.Api.Common.Application.Messaging;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;
using Support.Api.Modules.Operations.Infrastructure;
using Support.Api.Modules.Operations.Presentation;

namespace Support.Api.Modules.Operations;

/// <summary>Wires the Operations module: bound approvals, approved execution, write tools, and GET /api/v1/operations/{id}.</summary>
internal static class OperationsModule
{
    public static IServiceCollection AddOperationsModule(this IServiceCollection services)
    {
        services.AddSingleton<IOperationStore, SqliteOperationStore>();
        services.AddSingleton<OperationMetrics>();
        services.AddSingleton<OperationService>();
        services.AddSingleton<OperationTools>();
        services.AddSingleton<IQueryHandler<GetOperationQuery, Operation>, GetOperationQueryHandler>();
        return services;
    }

    public static WebApplication MapOperationsModule(this WebApplication app)
    {
        app.MapOperationEndpoints();
        return app;
    }
}
