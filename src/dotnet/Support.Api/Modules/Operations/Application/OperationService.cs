using System.Text.Json.Nodes;
using Support.Api.Common.Domain;
using Support.Api.Modules.Identity.Application;
using Support.Api.Modules.Operations.Domain;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Operations.Application;

/// <summary>
/// Turns native approval requests into bound operations, and runs approved operations. Business authorization lives
/// here and in the owning modules' write handlers, independent of the model and of the native approval gate.
/// </summary>
public sealed class OperationService(
    IOperationStore store,
    IEnumerable<IWriteHandler> handlers,
    IPrincipalSource principals,
    SupportPolicy policy,
    TimeProvider clock,
    OperationMetrics metrics)
{
    private readonly Dictionary<OperationKind, IWriteHandler> handlersByKind = handlers.ToDictionary(handler => handler.Kind);

    /// <summary>
    /// Validates the model's tool call and checks eligibility <em>before</em> asking for approval. Only an eligible,
    /// valid request becomes an operation awaiting approval, bound to this customer, conversation, kind, and exact arguments.
    /// </summary>
    public async Task<Result<Operation>> ProposeAsync(
        string conversationId, RouteTarget specialist, string sourceTurnId, string toolName, IReadOnlyDictionary<string, object?> toolArguments, CancellationToken cancellationToken)
    {
        if (WriteTools.KindOf(toolName) is not { } kind)
        {
            return Result.Failure<Operation>(WriteErrors.InvalidArguments("Unknown write tool."));
        }

        var arguments = WriteTools.Map(kind, toolArguments, policy.TicketPriorities);
        if (arguments.IsFailure)
        {
            return Result.Failure<Operation>(arguments.Error);
        }

        var check = await handlersByKind[kind].CheckAsync(arguments.Value, cancellationToken).ConfigureAwait(false);
        if (check.IsFailure)
        {
            metrics.Record(kind, check.Error.Code);
            return Result.Failure<Operation>(check.Error);
        }

        var now = clock.GetUtcNow();
        var operation = new Operation(
            Ids("op_"), Ids("appr_"), conversationId, principals.Current.CustomerId, kind, arguments.Value, check.Value,
            specialist, sourceTurnId, now, now.AddMinutes(policy.ApprovalExpiryMinutes));
        store.Add(operation);
        metrics.Record(kind, "awaiting_approval");
        return Result.Success(operation);
    }

    /// <summary>
    /// Runs the write a tool was asked to perform, but only if the trusted <see cref="ApprovalScope"/> holds an approved
    /// operation for this customer with the same kind and exactly the same arguments, and only once. The owning module
    /// re-checks eligibility; the operation records the factual result or the failure.
    /// </summary>
    public async Task<Result<JsonObject>> ExecuteApprovedAsync(OperationKind kind, OperationArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var operation = ApprovalScope.Current;
        if (operation is null || operation.Kind != kind || !operation.Arguments.Equals(arguments) ||
            !string.Equals(operation.OwnerCustomerId, principals.Current.CustomerId, StringComparison.Ordinal))
        {
            return Result.Failure<JsonObject>(Error.Conflict("not_approved", "This exact action has not been approved."));
        }

        if (!operation.TryBeginExecution())
        {
            return operation.Result is { } done ? Result.Success(done) : Result.Failure<JsonObject>(Error.Conflict("not_approved", "This action is not awaiting execution."));
        }

        var result = await handlersByKind[kind].ExecuteAsync(operation.Arguments, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (result.IsSuccess)
        {
            operation.Commit(result.Value, now);
            metrics.Record(kind, "committed");
        }
        else
        {
            operation.Fail(result.Error.Code is "not_eligible" or "not_found" ? result.Error.Code : "internal_error", result.Error.Description, now);
            metrics.Record(kind, "failed");
        }

        return result;
    }

    private static string Ids(string prefix) =>
        prefix + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}
