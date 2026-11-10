using System.Text.Json.Nodes;
using Support.Api.Common.Domain;
using Support.Api.Modules.Operations.Domain;

namespace Support.Api.Modules.Operations.Application;

/// <summary>
/// One kind of write, implemented by the module that owns the data (Orders cancels, Billing refunds, Technical creates
/// tickets). Both methods use the server's principal. <see cref="CheckAsync"/> runs before approval is requested;
/// <see cref="ExecuteAsync"/> checks again, authoritatively, and changes state only if still allowed.
/// </summary>
public interface IWriteHandler
{
    OperationKind Kind { get; }

    /// <summary>Validates and checks eligibility without changing anything. Success carries a human-readable summary.</summary>
    Task<Result<string>> CheckAsync(OperationArguments arguments, CancellationToken cancellationToken);

    /// <summary>Re-checks and performs the write. Success carries the contract result object.</summary>
    Task<Result<JsonObject>> ExecuteAsync(OperationArguments arguments, CancellationToken cancellationToken);
}
