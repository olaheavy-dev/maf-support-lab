using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Application.Streaming;
using Support.Api.Modules.Conversations.Domain;

namespace Support.Api.Modules.Conversations.Application.SubmitTurn;

/// <summary>
/// Run one customer message. The message has already passed contract validation in Presentation. The observer receives
/// the turn's live events; a plain JSON request passes <see cref="TurnObserver.None"/>.
/// </summary>
public sealed record SubmitTurnCommand(string ConversationId, string ClientTurnId, string Message, ITurnObserver Observer) : ICommand<Turn>
{
    public SubmitTurnCommand(string conversationId, string clientTurnId, string message)
        : this(conversationId, clientTurnId, message, TurnObserver.None)
    {
    }
}
