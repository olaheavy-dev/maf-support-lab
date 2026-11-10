using Support.Api.Modules.Operations.Domain;

namespace Support.Api.Modules.Operations.Application;

/// <summary>
/// Where operations are kept: durably (L11), so an approval survives a restart. Every state change is a guarded
/// update: it is stored only if the stored status is still the one the change started from.
/// </summary>
public interface IOperationStore
{
    void Add(Operation operation);

    Operation? Find(string operationId);

    Operation? FindByApproval(string approvalId);

    IReadOnlyList<Operation> ForConversation(string conversationId);

    /// <summary>
    /// Stores the operation's new state only if the stored status is still <paramref name="expected"/>. If it is not
    /// (another run got there first), the instance is reloaded with the stored state and false is returned.
    /// Inside <see cref="Common.Application.IUnitOfWork.AtomicallyAsync{T}"/> it joins that transaction.
    /// </summary>
    bool TryUpdate(Operation operation, OperationStatus expected);
}
