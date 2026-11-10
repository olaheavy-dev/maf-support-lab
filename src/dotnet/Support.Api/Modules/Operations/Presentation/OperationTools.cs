using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Support.Api.Common.Domain;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;

namespace Support.Api.Modules.Operations.Presentation;

/// <summary>
/// The model's only write tools. Each is a native <see cref="ApprovalRequiredAIFunction"/>, so the framework pauses
/// for the customer's approval instead of running it. When it does run (after approval), it can still only perform the
/// operation the customer approved, with exactly these arguments, once: <see cref="OperationService.ExecuteApprovedAsync"/>.
/// None takes a customer id or a "confirmed" flag: approval comes from the customer, never from the model.
/// </summary>
public sealed partial class OperationTools(OperationService operations, SupportPolicy policy, ILogger<OperationTools> logger)
{
    public AIFunction CancelOrder => new ApprovalRequiredAIFunction(AIFunctionFactory.Create(CancelOrderAsync, WriteTools.CancelOrder));

    public AIFunction RefundOrder => new ApprovalRequiredAIFunction(AIFunctionFactory.Create(RefundOrderAsync, WriteTools.RefundOrder));

    public AIFunction CreateTicket => new ApprovalRequiredAIFunction(AIFunctionFactory.Create(CreateTicketAsync, WriteTools.CreateTicket));

    [Description("Cancel one of the current customer's orders. Requires the customer's approval; only orders still processing can be cancelled.")]
    public Task<WriteResult> CancelOrderAsync(
        [Description("The order id, in the form ORD-1234.")] string orderId,
        CancellationToken cancellationToken = default) =>
        RunAsync(OperationKind.CancelOrder, WriteTools.Cancel(orderId), cancellationToken);

    [Description("Refund one of the current customer's delivered orders in full. Requires the customer's approval.")]
    public Task<WriteResult> RefundOrderAsync(
        [Description("The order id, in the form ORD-1234.")] string orderId,
        [Description("The customer's reason, in their words.")] string reason,
        CancellationToken cancellationToken = default) =>
        RunAsync(OperationKind.RefundOrder, WriteTools.Refund(orderId, reason), cancellationToken);

    [Description("Open a support ticket for a problem the knowledge base could not solve. Requires the customer's approval.")]
    public Task<WriteResult> CreateTicketAsync(
        [Description("A short subject line.")] string subject,
        [Description("What is wrong, in the customer's words.")] string description,
        [Description("low, normal, or high.")] string priority = "normal",
        [Description("A related order id (ORD-1234), if any.")] string? relatedOrderId = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(OperationKind.CreateTicket, WriteTools.Ticket(subject, description, priority, relatedOrderId, policy.TicketPriorities), cancellationToken);

    private async Task<WriteResult> RunAsync(OperationKind kind, Result<OperationArguments> arguments, CancellationToken cancellationToken)
    {
        if (arguments.IsFailure)
        {
            return new WriteResult("invalid_arguments", null, arguments.Error.Description);
        }

        var result = await operations.ExecuteApprovedAsync(kind, arguments.Value, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return new WriteResult("committed", result.Value, null);
        }

        Log.WriteRefused(logger, OperationNames.Of(kind), result.Error.Code);
        return new WriteResult(result.Error.Code == "not_approved" ? "not_approved" : "failed", null, result.Error.Description);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Write tool {Kind} did not change anything: {Code}")]
        public static partial void WriteRefused(ILogger logger, string kind, string code);
    }
}

/// <summary>What the model sees after a write tool runs: the factual outcome only.</summary>
public sealed record WriteResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("result")] JsonObject? Result,
    [property: JsonPropertyName("detail")] string? Detail);
