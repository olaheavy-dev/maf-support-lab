namespace Support.Api.Modules.Triage.Domain;

/// <summary>The contract's wire names for routing values (snake_case, as in contracts/schemas).</summary>
public static class Names
{
    public static string Of(RouteTarget target) => target switch
    {
        RouteTarget.Orders => "orders",
        RouteTarget.Billing => "billing",
        RouteTarget.Technical => "technical",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    /// <summary>The route target with this contract name, if there is one.</summary>
    public static bool TryParseTarget(string name, out RouteTarget target)
    {
        foreach (var candidate in Enum.GetValues<RouteTarget>())
        {
            if (Of(candidate) == name)
            {
                target = candidate;
                return true;
            }
        }

        target = default;
        return false;
    }

    public static string Of(UnroutedReason reason) => reason switch
    {
        UnroutedReason.InvalidModelOutput => "invalid_model_output",
        UnroutedReason.AmbiguousRequest => "ambiguous_request",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    public static string Of(RequestedAction action) => action switch
    {
        RequestedAction.OrderStatus => "order_status",
        RequestedAction.CancelOrder => "cancel_order",
        RequestedAction.RefundOrder => "refund_order",
        RequestedAction.Invoice => "invoice",
        RequestedAction.PaymentHistory => "payment_history",
        RequestedAction.Troubleshooting => "troubleshooting",
        RequestedAction.CreateTicket => "create_ticket",
        RequestedAction.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    public static string Of(ReplyKind kind) => kind switch
    {
        ReplyKind.Answer => "answer",
        ReplyKind.NotFound => "not_found",
        ReplyKind.Clarification => "clarification",
        ReplyKind.EscalationOffer => "escalation_offer",
        ReplyKind.NotEligible => "not_eligible",
        ReplyKind.ApprovalRequest => "approval_request",
        ReplyKind.ApprovalResult => "approval_result",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
