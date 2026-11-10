using Support.Api.Common.Domain;

namespace Support.Api.Modules.Conversations.Application;

public static class ConversationErrors
{
    /// <summary>Used for missing conversations and turns and for other customers' ones alike.</summary>
    public static readonly Error NotFound = Error.NotFound("not_found", "The resource does not exist or is not available to you.");

    /// <summary>One turn at a time per conversation: a different turn while one runs is rejected, never queued.</summary>
    public static readonly Error TurnInProgress = Error.Conflict("turn_in_progress", "Another turn is still running in this conversation.");

    public static readonly Error ApprovalMismatch = Error.Conflict("approval_mismatch", "The operation_id does not match this approval.");

    public static readonly Error ApprovalAlreadyDecided = Error.Conflict("approval_already_decided", "A different decision was already recorded for this approval.");

    public static readonly Error ApprovalExpired = Error.Gone("approval_expired", "The approval window has passed. Nothing was changed.");

    public static readonly Error ClientTurnIdReused = Error.Conflict("client_turn_id_reused", "This client_turn_id was already used with a different message.");
}
