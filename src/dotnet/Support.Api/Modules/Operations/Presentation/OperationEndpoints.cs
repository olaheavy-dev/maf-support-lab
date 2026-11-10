using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Presentation;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;

namespace Support.Api.Modules.Operations.Presentation;

internal static class OperationEndpoints
{
    public static IEndpointRouteBuilder MapOperationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/operations/{operationId}", async (string operationId, IQueryHandler<GetOperationQuery, Operation> handler, CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new GetOperationQuery(operationId), cancellationToken).ConfigureAwait(false);
            return result.Match(operation => ApiResults.Ok(OperationResponse.From(operation)), ApiResults.Problem);
        });
        return app;
    }
}

/// <summary>The contract's operation shape (contracts/schemas/operation.schema.json).</summary>
public sealed record OperationResponse(
    string ContractVersion,
    string OperationId,
    string ConversationId,
    string ApprovalId,
    string Kind,
    JsonObject Arguments,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ExpiresAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OperationFailure? Failure)
{
    public static OperationResponse From(Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return new(
            Common.Presentation.ContractVersion.Current, operation.OperationId, operation.ConversationId, operation.ApprovalId,
            OperationNames.Of(operation.Kind), operation.Arguments.ToJson(), OperationNames.Of(operation.Status),
            operation.CreatedAt, operation.UpdatedAt,
            operation.Status == OperationStatus.AwaitingApproval ? operation.ExpiresAt : null,
            operation.Status == OperationStatus.Committed ? (JsonObject?)operation.Result?.DeepClone() : null,
            operation.Failure is { } failure && operation.Status == OperationStatus.Failed ? new OperationFailure(failure.Code, failure.Detail) : null);
    }
}

public sealed record OperationFailure(string Code, string Detail);

/// <summary>The contract's pending approval shape (common.schema.json#/$defs/approval).</summary>
public sealed record PendingApproval(string ApprovalId, string OperationId, string Kind, JsonObject Arguments, string Summary, DateTimeOffset ExpiresAt)
{
    public static PendingApproval From(Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return new(operation.ApprovalId, operation.OperationId, OperationNames.Of(operation.Kind), operation.Arguments.ToJson(), operation.Summary, operation.ExpiresAt);
    }
}

/// <summary>The contract's operation summary shape (common.schema.json#/$defs/operation_summary).</summary>
public sealed record OperationSummary(string OperationId, string Kind, string Status)
{
    public static OperationSummary From(Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return new(operation.OperationId, OperationNames.Of(operation.Kind), OperationNames.Of(operation.Status));
    }
}
