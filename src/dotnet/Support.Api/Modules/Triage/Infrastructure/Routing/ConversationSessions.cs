using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;
using Support.Api.Common.Infrastructure.Persistence;

namespace Support.Api.Modules.Triage.Infrastructure.Routing;

/// <summary>
/// Each conversation's native agent sessions, one per specialist, created on first use and reused on later turns.
/// The agents themselves are shared singletons; only these sessions hold a conversation's history. Callers reach a
/// session only after the conversation's ownership was checked, and the one-turn-at-a-time rule means a session is
/// never used by two runs at once. Since L11 each session is serialized with MAF's <c>SerializeSessionAsync</c> after
/// every run and restored with <c>DeserializeSessionAsync</c> in a new process (the agents have stable ids for this).
/// </summary>
internal sealed class ConversationSessions(SupportDatabase database)
{
    private readonly ConcurrentDictionary<(string ConversationId, string Agent), Lazy<Task<AgentSession>>> sessions = new();

    public Task<AgentSession> GetOrCreateAsync(string conversationId, string agentName, AIAgent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return sessions.GetOrAdd(
            (conversationId, agentName),
            _ => new Lazy<Task<AgentSession>>(() => RestoreOrCreateAsync(conversationId, agentName, agent, cancellationToken))).Value;
    }

    /// <summary>Stores the session's current state (its bounded history), so the next process continues from it.</summary>
    public async Task SaveAsync(string conversationId, string agentName, AIAgent agent, AgentSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var state = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false);
        database.Execute(
            "INSERT INTO agent_sessions(conversation_id, agent, json) VALUES (@conversation, @agent, @json) ON CONFLICT(conversation_id, agent) DO UPDATE SET json = excluded.json",
            ("@conversation", conversationId), ("@agent", agentName), ("@json", state.GetRawText()));
    }

    /// <summary>For tests and diagnostics: how many sessions a conversation has in this process.</summary>
    public int CountFor(string conversationId) => sessions.Keys.Count(key => key.ConversationId == conversationId);

    private async Task<AgentSession> RestoreOrCreateAsync(string conversationId, string agentName, AIAgent agent, CancellationToken cancellationToken)
    {
        var stored = database.Query(
            "SELECT json FROM agent_sessions WHERE conversation_id = @conversation AND agent = @agent",
            reader => reader.GetString(0),
            ("@conversation", conversationId), ("@agent", agentName));
        if (stored.Count == 0)
        {
            return await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        using var document = JsonDocument.Parse(stored[0]);
        return await agent.DeserializeSessionAsync(document.RootElement.Clone(), cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
