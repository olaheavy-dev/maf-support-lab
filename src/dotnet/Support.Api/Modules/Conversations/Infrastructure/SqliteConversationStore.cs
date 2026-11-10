using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Support.Api.Common.Infrastructure.Persistence;
using Support.Api.Modules.Conversations.Application;
using Support.Api.Modules.Conversations.Domain;
using Support.Api.Modules.Identity.Domain;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Conversations.Infrastructure;

/// <summary>
/// Conversations in the local SQLite store (L11). A conversation is loaded once per process and then kept, because its
/// in-process object also coordinates the turn running right now (one at a time, duplicates wait for it); finished
/// turns and the context are written through on every turn, so a new process restores them.
/// </summary>
internal sealed class SqliteConversationStore(SupportDatabase database) : IConversationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly ConcurrentDictionary<string, Lazy<Conversation?>> loaded = new(StringComparer.Ordinal);

    public void Add(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        database.Execute(
            "INSERT INTO conversations(conversation_id, owner_customer_id, created_at, context_json) VALUES (@id, @owner, @created, @context)",
            ("@id", conversation.ConversationId), ("@owner", conversation.OwnerCustomerId),
            ("@created", conversation.CreatedAt.ToString("O", CultureInfo.InvariantCulture)), ("@context", JsonSerializer.Serialize(conversation.Context, Json)));
        loaded[conversation.ConversationId] = new Lazy<Conversation?>(conversation);
    }

    public Conversation? FindOwned(string conversationId, SupportPrincipal principal)
    {
        var conversation = loaded.GetOrAdd(conversationId, id => new Lazy<Conversation?>(() => Load(id))).Value;
        if (conversation is null)
        {
            loaded.TryRemove(conversationId, out _); // unknown now; it may be created later
            return null;
        }

        return conversation.IsOwnedBy(principal) ? conversation : null;
    }

    /// <summary>The turn and the context it produced are one transaction: a replayable turn never comes back without its context.</summary>
    public Turn SaveTurn(string conversationId, string clientKey, string message, Turn turn, ConversationContext contextAfter) =>
        database.Atomically(() => InsertTurn(conversationId, clientKey, message, turn, contextAfter));

    private Turn InsertTurn(string conversationId, string clientKey, string message, Turn turn, ConversationContext contextAfter)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var inserted = database.Execute(
            "INSERT INTO turns(turn_id, conversation_id, client_key, message, json) VALUES (@turn, @conversation, @key, @message, @json) ON CONFLICT(conversation_id, client_key) WHERE client_key IS NOT NULL DO NOTHING",
            ("@turn", turn.TurnId), ("@conversation", conversationId), ("@key", clientKey), ("@message", message),
            ("@json", JsonSerializer.Serialize(StoredTurnJson.From(turn), Json)));
        if (inserted == 0)
        {
            return database.Query(
                "SELECT json FROM turns WHERE conversation_id = @conversation AND client_key = @key",
                reader => JsonSerializer.Deserialize<StoredTurnJson>(reader.GetString(0), Json)!.ToTurn(),
                ("@conversation", conversationId), ("@key", clientKey))[0];
        }

        database.Execute(
            "UPDATE conversations SET context_json = @context WHERE conversation_id = @conversation",
            ("@conversation", conversationId), ("@context", JsonSerializer.Serialize(contextAfter, Json)));
        return turn;
    }

    private Conversation? Load(string conversationId)
    {
        var header = database.Query(
            "SELECT owner_customer_id, created_at, context_json FROM conversations WHERE conversation_id = @id",
            reader => (Owner: reader.GetString(0), CreatedAt: DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), Context: reader.GetString(2)),
            ("@id", conversationId));
        if (header.Count == 0)
        {
            return null;
        }

        var turns = database.Query(
            "SELECT client_key, message, json FROM turns WHERE conversation_id = @id",
            reader => new StoredTurn(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                JsonSerializer.Deserialize<StoredTurnJson>(reader.GetString(2), Json)!.ToTurn()),
            ("@id", conversationId));
        var (owner, createdAt, context) = header[0];
        return Conversation.Restore(conversationId, owner, createdAt, JsonSerializer.Deserialize<ConversationContext>(context, Json) ?? ConversationContext.Empty, turns);
    }

    /// <summary>A turn's stored shape: the route as plain values (the domain type has no public constructor).</summary>
    private sealed record StoredTurnJson(
        string ConversationId, string TurnId, string? ClientTurnId, RouteTarget? RouteTarget, UnroutedReason? UnroutedReason,
        ReplyKind ReplyKind, string ReplyText, string TraceId, DateTimeOffset CreatedAt, DateTimeOffset FinishedAt, TurnOutcome Outcome,
        string? PendingOperationId, string? ResumedFromTurnId, IReadOnlyList<string> OperationIds, TurnError? Error = null)
    {
        public static StoredTurnJson From(Turn turn) => new(
            turn.ConversationId, turn.TurnId, turn.ClientTurnId, turn.Route?.Target, turn.Route?.UnroutedReason, turn.ReplyKind, turn.ReplyText,
            turn.TraceId, turn.CreatedAt, turn.FinishedAt, turn.Outcome, turn.PendingOperationId, turn.ResumedFromTurnId, turn.OperationIds, turn.Error);

        public Turn ToTurn() => new(
            ConversationId, TurnId, ClientTurnId,
            RouteTarget is { } target ? TurnRoute.Routed(target) : UnroutedReason is { } reason ? TurnRoute.Unrouted(reason) : null,
            ReplyKind, ReplyText, TraceId, CreatedAt, FinishedAt)
        {
            Outcome = Outcome,
            PendingOperationId = PendingOperationId,
            ResumedFromTurnId = ResumedFromTurnId,
            OperationIds = OperationIds,
            Error = Error,
        };
    }
}
