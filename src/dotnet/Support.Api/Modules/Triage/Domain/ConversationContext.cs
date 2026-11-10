namespace Support.Api.Modules.Triage.Domain;

/// <summary>
/// What a conversation remembers between turns for routing (application state, not chat history). Only verified
/// facts go in: <see cref="VerifiedOrderId"/> is an order a tool actually found for this customer, never an id the
/// customer or the model merely mentioned.
/// </summary>
public sealed record ConversationContext(RouteTarget? LastTarget, string? VerifiedOrderId)
{
    public static readonly ConversationContext Empty = new(null, null);

    /// <summary>The context after a turn: a newly verified order replaces the old one; otherwise the old one stays.</summary>
    public ConversationContext After(SupportReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return new ConversationContext(reply.Route.Target ?? LastTarget, reply.VerifiedOrderId ?? VerifiedOrderId);
    }
}
