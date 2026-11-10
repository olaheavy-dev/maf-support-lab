using Support.Api.Common.Application.Messaging;
using Support.Api.Modules.Conversations.Domain;

namespace Support.Api.Modules.Conversations.Application.GetConversation;

public sealed record GetConversationQuery(string ConversationId) : IQuery<Conversation>;
