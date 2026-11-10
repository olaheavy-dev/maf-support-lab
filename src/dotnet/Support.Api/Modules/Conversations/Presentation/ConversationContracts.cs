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
    string Outcome,
    RouteResult Route,
    ReplyResult Reply,
    IReadOnlyList<OperationSummary> Operations,
    string TraceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset FinishedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResumedFromTurnId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PendingApproval? PendingApproval { get; init; }

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
            Outcome: turn.Outcome == TurnOutcome.AwaitingApproval ? "awaiting_approval" : "completed",
            RouteResult.From(turn.Route),
            new ReplyResult(Names.Of(turn.ReplyKind), turn.ReplyText),
            [.. touched.Select(OperationSummary.From)],
            turn.TraceId,
            turn.CreatedAt,
            turn.FinishedAt)
        {
            ResumedFromTurnId = turn.ResumedFromTurnId,
            PendingApproval = turn.Outcome == TurnOutcome.AwaitingApproval && turn.PendingOperationId is { } pending && operations.Find(pending) is { } operation
                ? PendingApproval.From(operation)
                : null,
        };
    }
}
