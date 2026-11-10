using Support.Api.Common.Application.Messaging;
using Support.Api.Modules.Conversations.Domain;

namespace Support.Api.Modules.Conversations.Application.GetTurn;

public sealed record GetTurnQuery(string ConversationId, string TurnId) : IQuery<Turn>;
