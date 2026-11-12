using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Support.Api.Common.Application;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;
using Support.Api.Modules.Triage.Application;
using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Triage.Infrastructure.Routing;

/// <summary>
/// Adapts native approval requests to the contract's bound approvals. When a specialist's model calls a write
/// tool, the framework pauses with a <see cref="ToolApprovalRequestContent"/> instead of running it. This gate then:
/// proposes it as an operation (valid arguments, the customer's own record, eligible now), which ends the turn
/// awaiting approval; or declines it natively with the factual reason, so nothing is proposed and the model explains.
/// One write per turn; extra requests are declined.
/// </summary>
internal sealed class ApprovalGate(OperationService operations, PendingNativeApprovals pending, IUnitOfWork unitOfWork)
{
    private const int MaxRounds = 3;

    public async Task<GateResult> AfterRunAsync(
        AIAgent agent, AgentSession? session, AgentResponse response, TriageOutcome request, RouteTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        string? declinedCode = null;
        for (var round = 0; round < MaxRounds; round++)
        {
            var requests = Requests(response);
            if (requests.Count == 0)
            {
                return new GateResult(response, null, declinedCode);
            }

            var reason = "Only one change can be requested at a time.";
            if (requests.Count == 1 && requests[0].ToolCall is FunctionCallContent call)
            {
                var native = requests[0];
                // The operation and its paused native request are stored together (Codex review on PR #77): an
                // approval is never listed as pending unless the request it would resume was stored too.
                var proposal = request.ConversationId is { } conversationId && request.TurnId is { } turnId && session is not null
                    ? await unitOfWork.AtomicallyAsync(
                        async () =>
                        {
                            var proposed = await operations.ProposeAsync(conversationId, target, turnId, call.Name, (IReadOnlyDictionary<string, object?>?)call.Arguments ?? new Dictionary<string, object?>(), cancellationToken).ConfigureAwait(false);
                            if (proposed.IsSuccess)
                            {
                                pending.Store(proposed.Value.OperationId, native);
                            }

                            return proposed;
                        },
                        cancellationToken).ConfigureAwait(false)
                    : Common.Domain.Result.Failure<Operation>(WriteErrors.NotInConversation);

                if (proposal.IsSuccess)
                {
                    return new GateResult(response, proposal.Value, null);
                }

                declinedCode = proposal.Error.Code;
                reason = proposal.Error.Description;
            }

            // Decline natively: the tool never runs, and the model is told why (and that it must not retry).
            response = await agent.RunAsync(
                new ChatMessage(ChatRole.User, [.. requests.Select(item => (AIContent)item.CreateResponse(false, reason))]),
                session,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return new GateResult(response, null, declinedCode);
    }

    /// <summary>Declines every approval request in a resumed run: one decision resumes one write, never a chain.</summary>
    public static async Task<AgentResponse> DeclineFollowUpRequestsAsync(AIAgent agent, AgentSession session, AgentResponse response, CancellationToken cancellationToken)
    {
        for (var round = 0; round < MaxRounds && Requests(response) is { Count: > 0 } requests; round++)
        {
            response = await agent.RunAsync(
                new ChatMessage(ChatRole.User, [.. requests.Select(item => (AIContent)item.CreateResponse(false, "The customer has just decided on this request. Do not ask again in this turn."))]),
                session,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    public static IReadOnlyList<ToolApprovalRequestContent> Requests(AgentResponse response) =>
        [.. response.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>()];
}

/// <summary>What happened at the gate: the final response, the proposed operation (if any), or why a write was declined.</summary>
internal sealed record GateResult(AgentResponse Response, Operation? Proposed, string? DeclinedCode);
