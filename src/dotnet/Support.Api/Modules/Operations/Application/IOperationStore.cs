using Support.Api.Modules.Operations.Domain;

namespace Support.Api.Modules.Operations.Application;

/// <summary>Where operations are kept. In memory today; durable in L11.</summary>
public interface IOperationStore
{
    void Add(Operation operation);

    Operation? Find(string operationId);

    Operation? FindByApproval(string approvalId);

    IReadOnlyList<Operation> ForConversation(string conversationId);
}
