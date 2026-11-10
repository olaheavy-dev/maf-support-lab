using System.Text.Json.Nodes;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Operations.Domain;

/// <summary>
/// A proposed write and its approval, bound when created to the customer, the conversation, the kind, and the exact
/// arguments. Its status is the authoritative record of what happened, whatever happened to any connection.
/// State changes are guarded: a decision is recorded once, and the write runs at most once.
/// </summary>
public sealed class Operation
{
    private readonly Lock gate = new();

    public Operation(
        string operationId, string approvalId, string conversationId, string ownerCustomerId, OperationKind kind,
        OperationArguments arguments, string summary, RouteTarget specialist, string sourceTurnId, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        OperationId = operationId;
        ApprovalId = approvalId;
        ConversationId = conversationId;
        OwnerCustomerId = ownerCustomerId;
        Kind = kind;
        Arguments = arguments;
        Summary = summary;
        Specialist = specialist;
        SourceTurnId = sourceTurnId;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public string OperationId { get; }

    public string ApprovalId { get; }

    public string ConversationId { get; }

    public string OwnerCustomerId { get; }

    public OperationKind Kind { get; }

    public OperationArguments Arguments { get; }

    public string Summary { get; }

    /// <summary>The specialist whose session holds the paused native request.</summary>
    public RouteTarget Specialist { get; }

    /// <summary>The turn that asked for approval.</summary>
    public string SourceTurnId { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public OperationStatus Status { get; private set; } = OperationStatus.AwaitingApproval;

    public ApprovalDecision? Decision { get; private set; }

    /// <summary>The turn that resumed after the decision, returned again when the same decision is repeated.</summary>
    public string? DecisionTurnId { get; private set; }

    public JsonObject? Result { get; private set; }

    public (string Code, string Detail)? Failure { get; private set; }

    public bool IsPending(DateTimeOffset now) => Status == OperationStatus.AwaitingApproval && now < ExpiresAt;

    /// <summary>Marks an undecided approval as expired once its window has passed. Returns true if it is expired now.</summary>
    public bool ExpireIfDue(DateTimeOffset now)
    {
        lock (gate)
        {
            if (Status == OperationStatus.AwaitingApproval && now >= ExpiresAt)
            {
                Status = OperationStatus.Expired;
                UpdatedAt = now;
            }

            return Status == OperationStatus.Expired;
        }
    }

    /// <summary>Records the customer's decision exactly once. Returns false if a decision was already recorded.</summary>
    public bool TryDecide(ApprovalDecision decision, DateTimeOffset now)
    {
        lock (gate)
        {
            if (Decision is not null || Status != OperationStatus.AwaitingApproval)
            {
                return false;
            }

            Decision = decision;
            Status = decision == ApprovalDecision.Approve ? OperationStatus.Running : OperationStatus.Rejected;
            UpdatedAt = now;
            return true;
        }
    }

    /// <summary>Claims the approved write for execution. Only the first claim succeeds, so it can never run twice.</summary>
    public bool TryBeginExecution()
    {
        lock (gate)
        {
            if (Status != OperationStatus.Running || Result is not null || Failure is not null || executing)
            {
                return false;
            }

            executing = true;
            return true;
        }
    }

    public void Commit(JsonObject result, DateTimeOffset now)
    {
        lock (gate)
        {
            Status = OperationStatus.Committed;
            Result = result;
            UpdatedAt = now;
        }
    }

    public void Fail(string code, string detail, DateTimeOffset now)
    {
        lock (gate)
        {
            Status = OperationStatus.Failed;
            Failure = (code, detail);
            UpdatedAt = now;
        }
    }

    public void RecordDecisionTurn(string turnId)
    {
        lock (gate)
        {
            DecisionTurnId ??= turnId;
        }
    }

    private bool executing;
}
