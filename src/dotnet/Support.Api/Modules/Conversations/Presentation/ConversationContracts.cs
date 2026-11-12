using Support.Api.Common.Application;
using System.Text.Json.Serialization;
using Support.Api.Common.Presentation;
using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;
using Support.Api.Modules.Operations.Presentation;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Conversations.Presentation;

/// <summary>Version-1 request and response shapes from contracts/schemas. Property names are snake_case on the wire.</summary>
public sealed record ConversationResponse(
    string ContractVersion,
    string ConversationId,
    string Backend,
    DateTimeOffset CreatedAt,
    string? ActiveTurnId,
    IReadOnlyList<PendingApproval> PendingApprovals)
{
    /// <summary>The conversation with its approvals still awaiting a decision (expired ones are marked expired and left out).</summary>
    public static ConversationResponse From(Conversation conversation, IOperationStore operations, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(operations);
        var pending = operations.ForConversation(conversation.ConversationId)
            .Where(operation => !operation.ExpireIfDue(now) && operation.Status == OperationStatus.AwaitingApproval)
            .Where(operation => conversation.FindTurn(operation.SourceTurnId) is not null) // only approvals whose asking turn was stored
            .Select(PendingApproval.From)
            .ToList();
        return new(Common.Presentation.ContractVersion.Current, conversation.ConversationId, Common.Presentation.ContractVersion.Backend, conversation.CreatedAt, conversation.ActiveTurnId, pending);
    }
}

/// <summary>POST …/approvals/{approval_id} body. It carries no arguments: they cannot be changed here.</summary>
public sealed record ApprovalDecisionRequest(
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] string Decision,
    string? Reason = null);

public sealed record TurnRequest(
    [property: JsonRequired] string ClientTurnId,
    [property: JsonRequired] string Message);

public sealed record RouteResult(
    string Status,
    string? Target,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UnroutedReason = null)
{
    public static RouteResult From(TurnRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return route.Target is { } target
            ? new RouteResult("routed", Names.Of(target))
            : new RouteResult("unrouted", null, Names.Of(route.UnroutedReason!.Value));
    }
}

public sealed record ReplyResult(string Kind, string Text);

public sealed record TurnResult(
    string ContractVersion,
    string ConversationId,
    string TurnId,
    string? ClientTurnId,
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RouteResult? Route,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplyResult? Reply,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<OperationSummary>? Operations,
    string TraceId,
    DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? FinishedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResumedFromTurnId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PendingApproval? PendingApproval { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TurnErrorResult? Error { get; init; }

    /// <summary>The turn as finished, with each operation's <em>current</em> status (the authoritative record).</summary>
    public static TurnResult From(Turn turn, IOperationStore operations)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(operations);
        var touched = turn.OperationIds.Select(operations.Find).OfType<Operation>().ToList();
        return new(
            Common.Presentation.ContractVersion.Current,
            turn.ConversationId,
            turn.TurnId,
            turn.ClientTurnId,
            State: "finished",
            Outcome: Application.SubmitTurn.SubmitTurnCommandHandler.OutcomeName(turn.Outcome),
            turn.Route is { } route ? RouteResult.From(route) : null,
            // A failed turn has no reply (contract): its error says what happened.
            turn.Outcome == TurnOutcome.Failed ? null : new ReplyResult(Names.Of(turn.ReplyKind), turn.ReplyText),
            [.. touched.Select(OperationSummary.From)],
            turn.TraceId,
            turn.CreatedAt,
            turn.FinishedAt)
        {
            ResumedFromTurnId = turn.ResumedFromTurnId,
            PendingApproval = turn.Outcome == TurnOutcome.AwaitingApproval && turn.PendingOperationId is { } pending && operations.Find(pending) is { } operation
                ? PendingApproval.From(operation)
                : null,
            Error = turn.Error is { } error ? new TurnErrorResult(error.Code, error.Detail, error.RetryAfterSeconds) : null,
        };
    }

    /// <summary>A turn still running: what a status lookup can say before it finishes. No outcome yet.</summary>
    public static TurnResult Running(string conversationId, TurnReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        return new(
            Common.Presentation.ContractVersion.Current, conversationId, reservation.TurnId, reservation.ClientTurnId, State: "running",
            Outcome: null, Route: null, Reply: null, Operations: null,
            string.IsNullOrEmpty(reservation.TraceId) ? TraceIds.Current() : reservation.TraceId, reservation.StartedAt, FinishedAt: null)
        {
            ResumedFromTurnId = reservation.ResumedFromTurnId,
        };
    }
}

public sealed record TurnErrorResult(
    string Code,
    string Detail,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RetryAfterSeconds);
