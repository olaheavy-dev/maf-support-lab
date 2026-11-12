using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Support.Api.Modules.Agents.Application;

/// <summary>
/// How a specialist remembers a conversation. History lives in each conversation's own <see cref="AgentSession"/>,
/// never in the shared agent object, so one agent instance serves every conversation without mixing them.
/// </summary>
public static class ConversationHistory
{
    /// <summary>
    /// The most recent plain conversation messages a specialist keeps (about six exchanges); see
    /// <see cref="BoundedHistoryReducer"/> for what is dropped and why.
    /// </summary>
    public const int MaxMessages = 12;

    /// <summary>Author name marking a trusted, application-written context note (see <see cref="ContextNote"/>).</summary>
    public const string ContextAuthor = "support-context";

    /// <summary>A bounded in-memory history provider for a specialist agent. Context notes are never stored in it.</summary>
    public static ChatHistoryProvider CreateProvider() =>
        new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
        {
            ChatReducer = new BoundedHistoryReducer(MaxMessages),
            StorageInputRequestMessageFilter = messages => messages.Where(ShouldStore),
        });

    /// <summary>
    /// Which request messages to save after a run. Setting this filter replaces MAF's default, which skips messages
    /// that were loaded from history; without that, every turn would save the whole history again (a bug found and fixed).
    /// So: skip history-sourced messages, as the default does, and also skip this turn's context note.
    /// </summary>
    public static bool ShouldStore(ChatMessage message) =>
        message.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.ChatHistory && !IsContextNote(message);

    /// <summary>
    /// A system message carrying verified application state for this turn only, for example the order the
    /// conversation is about. Written by code from tool-verified facts, never from customer or model text.
    /// </summary>
    public static ChatMessage ContextNote(string text) => new(ChatRole.System, text) { AuthorName = ContextAuthor };

    public static bool IsContextNote(ChatMessage message) =>
        message is { Role.Value: "system", AuthorName: ContextAuthor };
}
