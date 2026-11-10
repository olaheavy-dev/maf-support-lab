using Support.Api.Common.Application.Messaging;
using Support.Api.Modules.Conversations.Domain;

namespace Support.Api.Modules.Conversations.Application.CreateConversation;

/// <summary>Start a conversation for the current customer. It carries no owner: the server decides that.</summary>
public sealed record CreateConversationCommand : ICommand<Conversation>;
