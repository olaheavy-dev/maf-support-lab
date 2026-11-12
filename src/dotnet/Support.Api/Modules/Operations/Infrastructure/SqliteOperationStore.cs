using System.Text.Json;
using System.Text.Json.Serialization;
using Support.Api.Common.Infrastructure.Persistence;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;

namespace Support.Api.Modules.Operations.Infrastructure;

/// <summary>
/// Operations in the local SQLite store. Each read loads the stored state (no in-process copy can go stale), and each
/// change is a guarded update on the status column, which is what makes a decision or a commit happen at most once
/// even across restarts or two hosts sharing one file.
/// </summary>
internal sealed class SqliteOperationStore(SupportDatabase database) : IOperationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public void Add(Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var snapshot = operation.ToSnapshot();
        database.Execute(
            "INSERT INTO operations(operation_id, approval_id, conversation_id, owner_customer_id, status, json) VALUES (@id, @approval, @conversation, @owner, @status, @json)",
            ("@id", snapshot.OperationId), ("@approval", snapshot.ApprovalId), ("@conversation", snapshot.ConversationId),
            ("@owner", snapshot.OwnerCustomerId), ("@status", OperationNames.Of(snapshot.Status)), ("@json", JsonSerializer.Serialize(snapshot, Json)));
    }

    // Every query is a constant; the key is always a bound parameter (no SQL is built from strings).
    private const string ByOperation = "SELECT json FROM operations WHERE operation_id = @key";
    private const string ByApproval = "SELECT json FROM operations WHERE approval_id = @key";
    private const string ByConversation = "SELECT json FROM operations WHERE conversation_id = @key";

    public Operation? Find(string operationId) => Load(ByOperation, operationId).FirstOrDefault();

    public Operation? FindByApproval(string approvalId) => Load(ByApproval, approvalId).FirstOrDefault();

    public IReadOnlyList<Operation> ForConversation(string conversationId) =>
        [.. Load(ByConversation, conversationId).OrderBy(operation => operation.CreatedAt)];

    public bool TryUpdate(Operation operation, OperationStatus expected)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var snapshot = operation.ToSnapshot();
        var changed = database.Execute(
            "UPDATE operations SET status = @status, json = @json WHERE operation_id = @id AND status = @expected",
            ("@id", snapshot.OperationId), ("@status", OperationNames.Of(snapshot.Status)), ("@json", JsonSerializer.Serialize(snapshot, Json)),
            ("@expected", OperationNames.Of(expected)));
        if (changed == 1)
        {
            return true;
        }

        var stored = Snapshots(ByOperation, snapshot.OperationId);
        if (stored.Count == 1)
        {
            operation.Reload(stored[0]);
        }

        return false;
    }

    private IEnumerable<Operation> Load(string query, string key) => Snapshots(query, key).Select(Operation.FromSnapshot);

    private IReadOnlyList<OperationSnapshot> Snapshots(string query, string key) =>
        database.Query(query, reader => JsonSerializer.Deserialize<OperationSnapshot>(reader.GetString(0), Json)!, ("@key", key));
}
