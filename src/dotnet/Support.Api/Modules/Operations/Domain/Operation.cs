using System.Text.Json.Nodes;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Operations.Domain;

/// <summary>
/// A proposed write and its approval, bound when created to the customer, the conversation, the kind, and the exact
/// arguments. Its status is the authoritative record of what happened, whatever happened to any connection.
/// State changes are guarded: a decision is recorded once, and the write runs at most once. The durable store
/// (L11) guards the same transitions by status, so they hold across restarts too.
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

    /// <summary>
    /// Closes an undecided approval now, whatever its window: used when the turn that asked for it was never stored
    /// (the process stopped first), so the customer never saw it and a retried turn asks again. Returns true if it closed it.
    /// </summary>
    public bool ExpireNow(DateTimeOffset now)
    {
        lock (gate)
        {
            if (Status != OperationStatus.AwaitingApproval)
            {
                return false;
            }

            Status = OperationStatus.Expired;
            UpdatedAt = now;
            return true;
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

    /// <summary>The operation's full state, for durable storage (L11).</summary>
    public OperationSnapshot ToSnapshot()
    {
        lock (gate)
        {
            return new OperationSnapshot(
                OperationId, ApprovalId, ConversationId, OwnerCustomerId, Kind, Arguments.Values.ToDictionary(), Summary, Specialist, SourceTurnId,
                CreatedAt, ExpiresAt, UpdatedAt, Status, Decision, DecisionTurnId, Result?.ToJsonString(), Failure?.Code, Failure?.Detail);
        }
    }

    public static Operation FromSnapshot(OperationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var operation = new Operation(
            snapshot.OperationId, snapshot.ApprovalId, snapshot.ConversationId, snapshot.OwnerCustomerId, snapshot.Kind,
            new OperationArguments(snapshot.Arguments), snapshot.Summary, snapshot.Specialist, snapshot.SourceTurnId, snapshot.CreatedAt, snapshot.ExpiresAt);
        operation.Reload(snapshot);
        return operation;
    }

    /// <summary>
    /// Replaces this instance's changing state with the stored state, e.g. after a guarded store update lost a race or
    /// an atomic write rolled back, so callers holding this instance never report a state that was not stored.
    /// </summary>
    public void Reload(OperationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            Status = snapshot.Status;
            Decision = snapshot.Decision;
            DecisionTurnId = snapshot.DecisionTurnId;
            Result = snapshot.ResultJson is null ? null : System.Text.Json.Nodes.JsonNode.Parse(snapshot.ResultJson)!.AsObject();
            Failure = snapshot.FailureCode is null ? null : (snapshot.FailureCode, snapshot.FailureDetail ?? string.Empty);
            UpdatedAt = snapshot.UpdatedAt;
            executing = false;
        }
    }
}

/// <summary>An operation as stored: plain values only.</summary>
public sealed record OperationSnapshot(
    string OperationId, string ApprovalId, string ConversationId, string OwnerCustomerId, OperationKind Kind,
    IReadOnlyDictionary<string, string> Arguments, string Summary, RouteTarget Specialist, string SourceTurnId,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset UpdatedAt, OperationStatus Status,
    ApprovalDecision? Decision, string? DecisionTurnId, string? ResultJson, string? FailureCode, string? FailureDetail);
