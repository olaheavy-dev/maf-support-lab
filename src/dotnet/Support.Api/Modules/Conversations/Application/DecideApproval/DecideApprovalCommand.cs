using Support.Api.Common.Application.Streaming;
using Support.Api.Common.Application.Messaging;
using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Operations.Domain;

namespace Support.Api.Modules.Conversations.Application.DecideApproval;

/// <summary>
/// The customer's decision on one approval. It names the operation it expects but cannot change any argument:
/// what runs is exactly what was bound when approval was requested.
/// </summary>
public sealed record DecideApprovalCommand(string ConversationId, string ApprovalId, string OperationId, ApprovalDecision Decision, string? Reason, ITurnObserver Observer)
    : ICommand<Turn>
{
    public DecideApprovalCommand(string conversationId, string approvalId, string operationId, ApprovalDecision decision, string? reason)
        : this(conversationId, approvalId, operationId, decision, reason, TurnObserver.None)
    {
    }
}
