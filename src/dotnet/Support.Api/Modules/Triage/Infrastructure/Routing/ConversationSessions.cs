using System.Collections.Concurrent;
using Microsoft.Agents.AI;

namespace Support.Api.Modules.Triage.Infrastructure.Routing;

/// <summary>
/// Each conversation's native agent sessions, one per specialist, created on first use and reused on later turns.
/// The agents themselves are shared singletons; only these sessions hold a conversation's history. Callers reach a
/// session only after the conversation's ownership was checked, and the one-turn-at-a-time rule means a session is
/// never used by two runs at once. In memory: lost on restart until durable state arrives in L11.
/// </summary>
internal sealed class ConversationSessions
{
    private readonly ConcurrentDictionary<(string ConversationId, string Agent), Lazy<Task<AgentSession>>> sessions = new();

    public Task<AgentSession> GetOrCreateAsync(string conversationId, string agentName, AIAgent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return sessions.GetOrAdd(
            (conversationId, agentName),
            _ => new Lazy<Task<AgentSession>>(() => agent.CreateSessionAsync(cancellationToken).AsTask())).Value;
    }

    /// <summary>For tests and diagnostics: how many sessions a conversation has.</summary>
    public int CountFor(string conversationId) => sessions.Keys.Count(key => key.ConversationId == conversationId);
}
