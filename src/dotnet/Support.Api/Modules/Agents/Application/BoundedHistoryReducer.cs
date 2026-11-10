using Microsoft.Extensions.AI;

namespace Support.Api.Modules.Agents.Application;

/// <summary>
/// Keeps a specialist's history bounded: the most recent <c>maxMessages</c> messages that carry plain conversation,
/// dropping tool calls, tool results, and context notes. Dropping tool traffic means trimming can never separate a
/// tool result from its call (which providers reject); the specialist re-reads fresh data with its tools instead.
/// Written here rather than using M.E.AI's MessageCountingChatReducer, which is marked experimental (MEAI001).
/// </summary>
public sealed class BoundedHistoryReducer(int maxMessages) : IChatReducer
{
    public int MaxMessages { get; } = maxMessages > 0 ? maxMessages : throw new ArgumentOutOfRangeException(nameof(maxMessages));

    public Task<IEnumerable<ChatMessage>> ReduceAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        IEnumerable<ChatMessage> kept = [.. messages.Where(IsConversation).TakeLast(MaxMessages)];
        return Task.FromResult(kept);
    }

    private static bool IsConversation(ChatMessage message) =>
        message.Role != ChatRole.System &&
        !ConversationHistory.IsContextNote(message) &&
        !message.Contents.Any(content => content is FunctionCallContent or FunctionResultContent);
}
