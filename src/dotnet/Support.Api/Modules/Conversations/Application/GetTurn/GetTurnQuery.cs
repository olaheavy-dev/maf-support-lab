using Support.Api.Common.Application.Messaging;
using Support.Api.Modules.Conversations.Domain;

namespace Support.Api.Modules.Conversations.Application.GetTurn;

public sealed record GetTurnQuery(string ConversationId, string TurnId) : IQuery<TurnStatus>;

/// <summary>A turn as a status lookup sees it (L12): finished (stored), or still running in this process.</summary>
public sealed record TurnStatus(string ConversationId, Turn? Finished, TurnReservation? Running);
