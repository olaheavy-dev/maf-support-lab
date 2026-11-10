using Support.Api.Common.Domain;

namespace Support.Api.Modules.Conversations.Application;

public static class ConversationErrors
{
    /// <summary>Used for missing conversations and turns and for other customers' ones alike.</summary>
    public static readonly Error NotFound = Error.NotFound("not_found", "The resource does not exist or is not available to you.");

    public static readonly Error ClientTurnIdReused = Error.Conflict("client_turn_id_reused", "This client_turn_id was already used with a different message.");
}
