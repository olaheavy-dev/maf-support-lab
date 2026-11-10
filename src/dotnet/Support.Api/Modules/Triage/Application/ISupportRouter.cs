using Support.Api.Common.Application.Streaming;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Triage.Application;

/// <summary>A decided approval to resume: the operation (by id) and the customer's optional reason.</summary>
public sealed record ApprovalResume(string OperationId, string? Reason);

/// <summary>Triage a customer message and run whichever specialist the routing rules choose.</summary>
public interface ISupportRouter
{
    /// <summary>Runs one message through the workflow. Progress and reply text go to <paramref name="observer"/> as they happen (L12).</summary>
    Task<SupportReply> RouteAsync(RoutingRequest request, ITurnObserver observer, CancellationToken cancellationToken);

    /// <summary>
    /// Resumes the specialist that paused for this operation's approval, in the same conversation session, with the
    /// customer's decision (L10). The operation's decision is already recorded and validated by the caller.
    /// </summary>
    Task<SupportReply> ResumeAsync(ApprovalResume decided, ITurnObserver observer, CancellationToken cancellationToken);
}
