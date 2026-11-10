using Support.Api.Common.Application.Messaging;
using Support.Api.Modules.Conversations.Application;
using Support.Api.Modules.Conversations.Application.CancelTurn;
using Support.Api.Modules.Conversations.Application.CreateConversation;
using Support.Api.Modules.Conversations.Application.DecideApproval;
using Support.Api.Modules.Conversations.Application.GetConversation;
using Support.Api.Modules.Conversations.Application.GetTurn;
using Support.Api.Modules.Conversations.Application.SubmitTurn;
using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Conversations.Infrastructure;
using Support.Api.Modules.Conversations.Presentation;

namespace Support.Api.Modules.Conversations;

/// <summary>Wires the Conversations module: the contract API's conversations and turns.</summary>
internal static class ConversationsModule
{
    public static IServiceCollection AddConversationsModule(this IServiceCollection services)
    {
        services.AddSingleton<IConversationStore, SqliteConversationStore>();
        services.AddSingleton<TurnMetrics>();
        services.AddSingleton<ICommandHandler<CreateConversationCommand, Conversation>, CreateConversationCommandHandler>();
        services.AddSingleton<IQueryHandler<GetConversationQuery, Conversation>, GetConversationQueryHandler>();
        services.AddSingleton<ICommandHandler<SubmitTurnCommand, Turn>, SubmitTurnCommandHandler>();
        services.AddSingleton<IQueryHandler<GetTurnQuery, TurnStatus>, GetTurnQueryHandler>();
        services.AddSingleton<ICommandHandler<CancelTurnCommand, string>, CancelTurnCommandHandler>();
        services.AddSingleton<ICommandHandler<DecideApprovalCommand, Turn>, DecideApprovalCommandHandler>();
        return services;
    }

    public static WebApplication MapConversationsModule(this WebApplication app)
    {
        app.MapConversationEndpoints();
        return app;
    }
}
