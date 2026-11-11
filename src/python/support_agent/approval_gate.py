"""Adapts native approval requests to the contract's bound approvals (L37, porting L10's ApprovalGate).

When a specialist's model calls a write tool, MAF pauses with a ``function_approval_request`` instead of running it.
The gate then either proposes it as a bound operation (valid arguments, the customer's own record, eligible now), which
ends the turn awaiting approval and keeps the native request for the decision, or declines it natively with the
factual reason, so nothing is proposed and the model explains. One write per turn; extra requests are declined, and
the number of decline rounds is bounded.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from agent_framework import AgentResponse, AgentSession, Content, Message, SupportsAgentRun

from support_agent.identity import current_principal
from support_agent.operations import Operation, OperationService, WriteError
from support_agent.routing import TriageOutcome

MAX_ROUNDS = 3


@dataclass(frozen=True, slots=True)
class GateResult:
    response: AgentResponse[Any]
    proposed: Operation | None = None
    declined_code: str | None = None
    declined_reason: str | None = None


def requests(response: AgentResponse[Any]) -> list[Content]:
    return [
        content
        for message in response.messages
        for content in message.contents
        if content.type == "function_approval_request"
    ]


def batch_of(response: AgentResponse[Any], pending: list[Content]) -> list[Content]:
    """All tool calls the model made in the turn that paused, in the model's order (L20, found live).

    In a mixed batch (say get_order_status and cancel_order in one response) MAF pauses the approval-required call,
    keeps the other calls in the session, and runs them when the batch is answered, sending their results too. So the
    answer must re-send every call of that batch, or Anthropic rejects a result whose call is missing (HTTP 400)."""
    paused = [request.function_call for request in pending if request.function_call is not None]
    paused_ids = {call.call_id for call in paused}
    for message in reversed(response.messages):
        calls = [content for content in message.contents if content.type == "function_call"]
        if any(call.call_id in paused_ids for call in calls):
            present = {call.call_id for call in calls}
            return calls + [call for call in paused if call.call_id not in present]
    return paused


def answer(pending: list[Content], approved: bool, batch: list[Content] | None = None) -> list[Message]:
    """The native answer to paused requests, preceded by the tool calls of the batch they belong to.

    Stored history keeps only plain text (L19), so the assistant message that made the calls is not in the session, and
    MAF does not re-add it. Anthropic rejects a tool result that does not directly follow its tool call (HTTP 400,
    found live in L37), so the calls are sent again, right before the decision that answers them: all calls of the
    batch, since MAF also answers the non-paused ones (found live in L20)."""
    calls = batch or [request.function_call for request in pending if request.function_call is not None]
    return [
        Message(role="assistant", contents=calls),
        Message(
            role="user", contents=[request.to_function_approval_response(approved=approved) for request in pending]
        ),
    ]


async def decline(
    agent: SupportsAgentRun, session: AgentSession | None, pending: list[Content], batch: list[Content] | None = None
) -> AgentResponse[Any]:
    """Answers native requests with "rejected": the tool never runs. No note is added: MAF places the rejection result
    after any other input, and a message between a tool call and its result is rejected by Anthropic (HTTP 400, found
    live in L37). The reply the customer sees states the factual reason, written by code (see the workflow)."""
    response: AgentResponse[Any] = await agent.run(answer(pending, approved=False, batch=batch), session=session)
    return response


async def gate(
    agent: SupportsAgentRun,
    session: AgentSession | None,
    response: AgentResponse[Any],
    outcome: TriageOutcome,
    target: str,
    operations: OperationService,
) -> GateResult:
    declined_code: str | None = None
    declined_reason: str | None = None
    for _ in range(MAX_ROUNDS):
        pending = requests(response)
        if not pending:
            return GateResult(response, None, declined_code, declined_reason)
        reason = "Only one change can be requested at a time."
        if len(pending) == 1 and pending[0].function_call is not None:
            call = pending[0].function_call
            if outcome.conversation_id is None or outcome.turn_id is None or session is None:
                declined_code, reason = "invalid_arguments", "Changes can only be requested in a support conversation."
            else:
                try:
                    arguments: dict[str, Any] = dict(call.parse_arguments() or {})
                    operation = operations.propose(
                        current_principal(),
                        outcome.conversation_id,
                        target,
                        outcome.turn_id,
                        call.name or "",
                        arguments,
                        pending[0],  # stored with the operation, in one transaction
                        batch_of(response, pending),
                    )
                except WriteError as error:
                    declined_code, reason = error.code, error.detail
                else:
                    return GateResult(response, operation)
        response = await decline(agent, session, pending, batch_of(response, pending))
        declined_reason = reason
    return GateResult(response, None, declined_code, declined_reason)
