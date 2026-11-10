using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Triage.Application;

/// <summary>
/// One message to route. <see cref="ConversationId"/> selects the conversation's native agent sessions (null means
/// a stateless run, as in a DevUI workflow chat); <see cref="Context"/> carries the verified facts from earlier turns.
/// Ownership of the conversation was already checked by the caller.
/// </summary>
public sealed record RoutingRequest(string? ConversationId, string Message, ConversationContext Context)
{
    /// <summary>The turn being run, so a proposed write can name the turn that asked for approval.</summary>
    public string? TurnId { get; init; }
}
