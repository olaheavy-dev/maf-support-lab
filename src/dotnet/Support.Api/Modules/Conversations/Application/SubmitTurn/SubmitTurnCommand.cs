using Support.Api.Common.Application.Messaging;
using Support.Api.Modules.Conversations.Domain;

namespace Support.Api.Modules.Conversations.Application.SubmitTurn;

/// <summary>Run one customer message. The message has already passed contract validation in Presentation.</summary>
public sealed record SubmitTurnCommand(string ConversationId, string ClientTurnId, string Message) : ICommand<Turn>;
