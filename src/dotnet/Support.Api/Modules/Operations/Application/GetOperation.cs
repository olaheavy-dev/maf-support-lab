using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Domain;
using Support.Api.Modules.Identity.Application;
using Support.Api.Modules.Operations.Domain;

namespace Support.Api.Modules.Operations.Application;

/// <summary>An operation's authoritative status, for its owner only.</summary>
public sealed record GetOperationQuery(string OperationId) : IQuery<Operation>;

internal sealed class GetOperationQueryHandler(IOperationStore store, IPrincipalSource principals, TimeProvider clock)
    : IQueryHandler<GetOperationQuery, Operation>
{
    public Task<Result<Operation>> Handle(GetOperationQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var operation = store.Find(query.OperationId);
        if (operation is null || !string.Equals(operation.OwnerCustomerId, principals.Current.CustomerId, StringComparison.Ordinal))
        {
            return Task.FromResult(Result.Failure<Operation>(Error.NotFound("not_found", "The operation does not exist or is not available to you.")));
        }

        if (operation.Decision is null && operation.ExpireIfDue(clock.GetUtcNow()))
        {
            store.TryUpdate(operation, OperationStatus.AwaitingApproval);
        }
        return Task.FromResult(Result.Success(operation));
    }
}
