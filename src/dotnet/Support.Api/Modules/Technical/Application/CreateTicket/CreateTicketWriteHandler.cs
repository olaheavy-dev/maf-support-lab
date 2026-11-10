using System.Text.Json.Nodes;
using Support.Api.Common.Domain;
using Support.Api.Modules.Identity.Application;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;
using Support.Api.Modules.Orders.Application;

namespace Support.Api.Modules.Technical.Application.CreateTicket;

/// <summary>Simulated ticket creation. A related order, if given, must be the customer's own.</summary>
internal sealed class CreateTicketWriteHandler(ITicketStore tickets, IOrderRepository orders, IPrincipalSource principals, TimeProvider clock) : IWriteHandler
{
    public OperationKind Kind => OperationKind.CreateTicket;

    public Task<Result<string>> CheckAsync(OperationArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return Task.FromResult(RelatedOrderIsOwned(arguments)
            ? Result.Success($"Open a {arguments["priority"]}-priority support ticket: {arguments["subject"]}")
            : Result.Failure<string>(WriteErrors.NotFound));
    }

    public Task<Result<JsonObject>> ExecuteAsync(OperationArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!RelatedOrderIsOwned(arguments))
        {
            return Task.FromResult(Result.Failure<JsonObject>(WriteErrors.NotFound));
        }

        var ticket = tickets.Create(principals.Current.CustomerId, arguments["subject"]!, arguments["description"]!, arguments["priority"]!, arguments["related_order_id"], clock.GetUtcNow());
        return Task.FromResult(Result.Success(new JsonObject { ["ticket_id"] = ticket.TicketId }));
    }

    private bool RelatedOrderIsOwned(OperationArguments arguments) =>
        arguments["related_order_id"] is not { } id || (orders.Find(id) is { } order && order.CustomerId == principals.Current.CustomerId);
}
