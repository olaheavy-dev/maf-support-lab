using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace Support.Api.Modules.Triage.Infrastructure.Routing;

/// <summary>
/// The paused native approval requests (MAF/M.E.AI <see cref="ToolApprovalRequestContent"/>), keyed by our operation id.
/// Answering one with <c>CreateResponse</c> resumes the framework's function invocation. In memory until L11.
/// </summary>
internal sealed class PendingNativeApprovals
{
    private readonly ConcurrentDictionary<string, ToolApprovalRequestContent> requests = new(StringComparer.Ordinal);

    public void Store(string operationId, ToolApprovalRequestContent request) => requests[operationId] = request;

    public ToolApprovalRequestContent? Take(string operationId) => requests.TryRemove(operationId, out var request) ? request : null;
}
