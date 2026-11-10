using System.Collections.Concurrent;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;

namespace Support.Api.Modules.Operations.Infrastructure;

/// <summary>Process-memory operations: lost on restart. Durable operation records arrive in L11.</summary>
internal sealed class InMemoryOperationStore : IOperationStore
{
    private readonly ConcurrentDictionary<string, Operation> byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Operation> byApproval = new(StringComparer.Ordinal);

    public void Add(Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        byId[operation.OperationId] = operation;
        byApproval[operation.ApprovalId] = operation;
    }

    public Operation? Find(string operationId) => byId.GetValueOrDefault(operationId);

    public Operation? FindByApproval(string approvalId) => byApproval.GetValueOrDefault(approvalId);

    public IReadOnlyList<Operation> ForConversation(string conversationId) =>
        [.. byId.Values.Where(operation => operation.ConversationId == conversationId).OrderBy(operation => operation.CreatedAt)];
}
