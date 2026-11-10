using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;

namespace Support.Api.Modules.Conversations.Application;

/// <summary>
/// An approval is actionable only once the turn that asked for it was stored. If the process stopped after the
/// proposal was saved but before its turn was (Codex review on PR #77), the customer never saw it, and retrying that
/// turn asks again with a new operation. The orphan is closed as expired, so two approvals for one request can never
/// both run. Called while the caller holds the conversation's turn, so no turn of this conversation is in flight.
/// </summary>
internal static class OrphanedApprovals
{
    public static bool IsOrphan(Conversation conversation, Operation operation) =>
        operation.Status == OperationStatus.AwaitingApproval && conversation.FindTurn(operation.SourceTurnId) is null;

    public static void Expire(Conversation conversation, IOperationStore operations, DateTimeOffset now)
    {
        foreach (var operation in operations.ForConversation(conversation.ConversationId).Where(operation => IsOrphan(conversation, operation)))
        {
            Expire(operation, operations, now);
        }
    }

    /// <summary>
    /// Closes the approvals a turn proposed when that turn ends failed or cancelled (Codex review on PR #78): an
    /// unsuccessful turn never leaves an actionable approval behind, and its session may not hold the paused request.
    /// </summary>
    public static void ExpireFromTurn(string conversationId, string turnId, IOperationStore operations, DateTimeOffset now)
    {
        foreach (var operation in operations.ForConversation(conversationId)
            .Where(operation => operation.Status == OperationStatus.AwaitingApproval && operation.SourceTurnId == turnId))
        {
            Expire(operation, operations, now);
        }
    }

    public static void Expire(Operation operation, IOperationStore operations, DateTimeOffset now)
    {
        if (operation.ExpireNow(now))
        {
            operations.TryUpdate(operation, OperationStatus.AwaitingApproval);
        }
    }
}
