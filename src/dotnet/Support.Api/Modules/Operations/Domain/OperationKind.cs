namespace Support.Api.Modules.Operations.Domain;

/// <summary>The simulated writes. Every one of them requires the customer's approval in this baseline.</summary>
public enum OperationKind
{
    CancelOrder,
    RefundOrder,
    CreateTicket,
}

/// <summary>Authoritative operation state (contract operation_status).</summary>
public enum OperationStatus
{
    AwaitingApproval,
    Rejected,
    Expired,
    Running,
    Committed,
    Failed,
}

public enum ApprovalDecision
{
    Approve,
    Reject,
}

public static class OperationNames
{
    public static string Of(OperationKind kind) => kind switch
    {
        OperationKind.CancelOrder => "cancel_order",
        OperationKind.RefundOrder => "refund_order",
        OperationKind.CreateTicket => "create_ticket",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static string Of(OperationStatus status) => status switch
    {
        OperationStatus.AwaitingApproval => "awaiting_approval",
        OperationStatus.Rejected => "rejected",
        OperationStatus.Expired => "expired",
        OperationStatus.Running => "running",
        OperationStatus.Committed => "committed",
        OperationStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static string Of(ApprovalDecision decision) => decision == ApprovalDecision.Approve ? "approve" : "reject";
}
