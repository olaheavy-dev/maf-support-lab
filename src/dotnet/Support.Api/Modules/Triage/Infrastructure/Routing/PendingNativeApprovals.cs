using System.Text.Json;
using Microsoft.Extensions.AI;
using Support.Api.Common.Infrastructure.Persistence;

namespace Support.Api.Modules.Triage.Infrastructure.Routing;

/// <summary>
/// The paused native approval requests (MAF/M.E.AI <see cref="ToolApprovalRequestContent"/>), keyed by our operation id.
/// Answering one with <c>CreateResponse</c> resumes the framework's function invocation. Stored durably (L11) in the
/// framework's own JSON form, so a decision made after a restart still answers the exact paused tool call. A request
/// stays stored until its operation's resumed run finishes, so a run interrupted by a restart can be resumed again;
/// the operation's guarded commit, not this store, is what stops a write from applying twice.
/// </summary>
internal sealed class PendingNativeApprovals(SupportDatabase database)
{
    public void Store(string operationId, ToolApprovalRequestContent request) =>
        database.Execute(
            "INSERT INTO native_approval_requests(operation_id, json) VALUES (@id, @json) ON CONFLICT(operation_id) DO UPDATE SET json = excluded.json",
            ("@id", operationId), ("@json", JsonSerializer.Serialize<AIContent>(request, AIJsonUtilities.DefaultOptions)));

    public ToolApprovalRequestContent? Find(string operationId)
    {
        var stored = database.Query("SELECT json FROM native_approval_requests WHERE operation_id = @id", reader => reader.GetString(0), ("@id", operationId));
        return stored.Count == 0 ? null : JsonSerializer.Deserialize<AIContent>(stored[0], AIJsonUtilities.DefaultOptions) as ToolApprovalRequestContent;
    }

    public void Remove(string operationId) =>
        database.Execute("DELETE FROM native_approval_requests WHERE operation_id = @id", ("@id", operationId));
}
