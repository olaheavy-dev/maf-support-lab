"""The version-1 contract API (L18; approvals and operations since L37). JSON only.

Every request body is validated against the shared contract schema before anything runs. Another customer's
conversation, turn, approval, or operation is 404, exactly like a missing one. A turn that runs and fails is a result
(200, outcome failed). Each turn runs as its own task (a dropped or cancelled request never stops it) with the
caller's principal pinned. Approval decisions are bound to the customer, the conversation, the operation, and one
decision; repeating the same decision returns the same turn and never runs the write twice.
"""

from __future__ import annotations

import asyncio
import json
import logging
import secrets
from datetime import UTC, datetime
from typing import Any

from agent_framework import AgentResponse
from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse

from support_agent import contract
from support_agent.agents import SupportAgents
from support_agent.approval_gate import answer, decline, requests
from support_agent.conversations import Claim, Conversation, ConversationStore, Reservation, Turn, TurnError, now
from support_agent.dependencies import Caller
from support_agent.identity import Principal, pinned
from support_agent.operations import Decision, Operation, OperationService, Status, approval_scope
from support_agent.routing import TurnRoute
from support_agent.workflow import FALLBACK, RoutingRequest, SupportReply, build_workflow

log = logging.getLogger(__name__)

TITLES = {
    "invalid_request": "Invalid request",
    "not_found": "Not found",
    "client_turn_id_reused": "client_turn_id reused",
    "turn_in_progress": "Turn in progress",
    "approval_mismatch": "Approval mismatch",
    "approval_already_decided": "Approval already decided",
    "approval_expired": "Approval expired",
}
STATUS = {
    "invalid_request": 400,
    "not_found": 404,
    "client_turn_id_reused": 409,
    "turn_in_progress": 409,
    "approval_mismatch": 409,
    "approval_already_decided": 409,
    "approval_expired": 410,
}
FAILED = TurnError("internal_error", "Something went wrong while handling your message. Please try again.")


def trace_id() -> str:
    """A trace id (32 hex). Python OpenTelemetry arrives later (L40); until then it is generated here."""
    return secrets.token_hex(16)


def problem(code: str, detail: str, errors: list[contract.SchemaError] | None = None) -> JSONResponse:
    body: dict[str, Any] = {
        "contract_version": contract.CONTRACT_VERSION,
        "type": f"https://contracts.maf-support-lab.example/v1/errors/{code}",
        "title": TITLES[code],
        "status": STATUS[code],
        "code": code,
        "detail": detail,
        "trace_id": trace_id(),
    }
    if errors:
        body["errors"] = [{"pointer": e.pointer, "message": e.message} for e in errors[:20]]
    return JSONResponse(body, status_code=STATUS[code], media_type="application/problem+json")


def not_found() -> JSONResponse:
    return problem("not_found", "The resource does not exist or is not available to you.")


async def read_body(request: Request, schema: str) -> tuple[Any, JSONResponse | None]:
    raw = await request.body()
    try:
        body: Any = json.loads(raw or b"{}")
    except ValueError:
        return None, problem(
            "invalid_request", "The body is not valid JSON.", [contract.SchemaError("/", "Invalid JSON.")]
        )
    found = contract.errors(schema, body)
    return body, (problem("invalid_request", "The request does not match the contract.", found) if found else None)


running_turns: set[asyncio.Task[None]] = set()


def detach(work: Any) -> None:
    """Runs a turn as its own task: no request's cancellation can stop it or leave its result unset."""
    task: asyncio.Task[None] = asyncio.create_task(work)
    running_turns.add(task)
    task.add_done_callback(running_turns.discard)


def create_router(store: ConversationStore, agents: SupportAgents, operations: OperationService) -> APIRouter:
    router = APIRouter(prefix="/api/v1")
    ops = operations.store

    def turn_json(turn: Turn) -> JSONResponse:
        return JSONResponse(turn.to_contract(contract.CONTRACT_VERSION, ops))

    @router.post("/conversations", status_code=201)
    async def create_conversation(request: Request, principal: Caller) -> JSONResponse:
        _, invalid = await read_body(request, "create-conversation-request")
        if invalid is not None:
            return invalid
        conversation = store.start(principal)
        return JSONResponse(conversation.to_contract(contract.CONTRACT_VERSION, contract.BACKEND, ops), status_code=201)

    @router.get("/conversations/{conversation_id}")
    async def get_conversation(conversation_id: str, principal: Caller) -> JSONResponse:
        conversation = store.find_owned(conversation_id, principal)
        if conversation is None:
            return not_found()
        return JSONResponse(conversation.to_contract(contract.CONTRACT_VERSION, contract.BACKEND, ops))

    @router.post("/conversations/{conversation_id}/turns")
    async def submit_turn(conversation_id: str, request: Request, principal: Caller) -> JSONResponse:
        # A conversation you can't see is 404 before anything else, so the body's validity reveals nothing about it.
        conversation = store.find_owned(conversation_id, principal)
        if conversation is None:
            return not_found()
        body, invalid = await read_body(request, "turn-request")
        if invalid is not None:
            return invalid
        message: str = body["message"]
        claim, outcome = conversation.reserve(body["client_turn_id"], message)
        if outcome is Reservation.BUSY or claim is None:
            return problem("turn_in_progress", "Another turn is still running in this conversation.")
        if claim.message != message:
            return problem("client_turn_id_reused", "This client_turn_id was already used with a different message.")
        if outcome is Reservation.OWNER:
            detach(complete_turn(agents, operations, conversation, claim, message, principal))
        return turn_json(await asyncio.shield(claim.done))

    @router.get("/conversations/{conversation_id}/turns/{turn_id}")
    async def get_turn(conversation_id: str, turn_id: str, principal: Caller) -> JSONResponse:
        conversation = store.find_owned(conversation_id, principal)
        if conversation is None:
            return not_found()
        if (turn := conversation.turns.get(turn_id)) is not None:
            return turn_json(turn)
        if (running := conversation.running(turn_id)) is not None:
            return JSONResponse(running.running_contract(conversation_id, contract.CONTRACT_VERSION))
        return not_found()

    @router.post("/conversations/{conversation_id}/approvals/{approval_id}")
    async def decide(conversation_id: str, approval_id: str, request: Request, principal: Caller) -> JSONResponse:
        conversation = store.find_owned(conversation_id, principal)
        operation = ops.find_by_approval(approval_id)
        # Bound to this customer and this conversation: anyone else, or any other conversation, gets 404.
        if (
            conversation is None
            or operation is None
            or operation.conversation_id != conversation_id
            or operation.owner != principal.customer_id
        ):
            return not_found()
        body, invalid = await read_body(request, "approval-decision-request")
        if invalid is not None:
            return invalid
        if body["operation_id"] != operation.operation_id:
            return problem("approval_mismatch", "The operation_id does not match this approval.")
        decision = Decision(body["decision"])
        if operation.decision is not None and operation.decision is not decision:
            return problem("approval_already_decided", "A different decision was already recorded for this approval.")
        if operation.decision is None and operation.expire_if_due(datetime.now(UTC)):
            return problem("approval_expired", "The approval window has passed. Nothing was changed.")
        if operation.source_turn_id not in conversation.turns:
            return not_found()  # the asking turn never finished: this approval was never shown to the customer

        # One decision per approval: its key makes a repeated decision a duplicate (replay), never a second run.
        key = f"approval:{operation.approval_id}"
        claim, outcome = conversation.reserve(key, str(decision))
        if outcome is Reservation.BUSY or claim is None:
            return problem("turn_in_progress", "Another turn is still running in this conversation.")
        if outcome is Reservation.DUPLICATE:
            if claim.message != str(decision):
                return problem(
                    "approval_already_decided", "A different decision was already recorded for this approval."
                )
            return turn_json(await asyncio.shield(claim.done))
        if not operation.decide(decision, datetime.now(UTC)):
            conversation.abandon(key, claim)
            expired = operation.status is Status.EXPIRED
            return problem(
                "approval_expired" if expired else "approval_already_decided", "This approval can no longer be decided."
            )
        operation.decision_reason = body.get("reason")
        detach(complete_decision(agents, operations, conversation, claim, operation, principal))
        return turn_json(await asyncio.shield(claim.done))

    @router.get("/operations/{operation_id}")
    async def get_operation(operation_id: str, principal: Caller) -> JSONResponse:
        operation = ops.find(operation_id)
        if operation is None or operation.owner != principal.customer_id:
            return not_found()
        operation.expire_if_due(datetime.now(UTC))
        return JSONResponse(operation.to_contract(contract.CONTRACT_VERSION))

    return router


async def complete_turn(
    agents: SupportAgents,
    operations: OperationService,
    conversation: Conversation,
    claim: Claim,
    message: str,
    principal: Principal,
) -> None:
    """Runs the claimed turn to its stored result; whatever happens, the claim is completed exactly once."""
    try:
        turn, verified_order_id = await run_turn(agents, operations, conversation, claim, message, principal)
    except BaseException:
        conversation.complete(
            claim,
            Turn(
                conversation.conversation_id,
                claim.turn_id,
                claim.client_turn_id,
                None,
                None,
                None,
                claim.trace_id,
                claim.started_at,
                now(),
                "failed",
                FAILED,
            ),
        )
        raise
    conversation.complete(claim, turn, verified_order_id)


async def run_turn(
    agents: SupportAgents,
    operations: OperationService,
    conversation: Conversation,
    claim: Claim,
    message: str,
    principal: Principal,
) -> tuple[Turn, str | None]:
    """Runs the workflow as the caller (pinned) in this conversation's own sessions and context."""
    started = now()
    with pinned(principal):
        try:
            request = RoutingRequest(
                message, conversation.conversation_id, conversation.context, conversation.sessions, claim.turn_id
            )
            result = await build_workflow(agents, operations).run(request)
            reply: SupportReply = result.get_outputs()[0]
        except Exception:
            log.exception("Turn %s failed in conversation %s", claim.turn_id, conversation.conversation_id)
            return Turn(
                conversation.conversation_id,
                claim.turn_id,
                claim.client_turn_id,
                None,
                None,
                None,
                claim.trace_id,
                started,
                now(),
                "failed",
                FAILED,
            ), None
    outcome = "awaiting_approval" if reply.pending_operation_id else "completed"
    turn = Turn(
        conversation.conversation_id,
        claim.turn_id,
        claim.client_turn_id,
        reply.route,
        reply.kind,
        reply.text,
        claim.trace_id,
        started,
        now(),
        outcome,
        None,
        reply.pending_operation_id,
        reply.operation_ids,
    )
    return turn, reply.verified_order_id


def factual(operation: Operation) -> str:
    """What the customer is told from the operation record alone (when the model's words can't be used)."""
    match operation.status:
        case Status.COMMITTED:
            return f"Done. {operation.summary}"
        case Status.REJECTED:
            return f"Okay, I did not go ahead. Nothing has been changed: {operation.summary}"
        case Status.FAILED:
            return f"I couldn't complete that: {operation.failure[1] if operation.failure else ''} Nothing has been changed."
        case _:
            return "That request was not carried out. Nothing has been changed."


async def complete_decision(
    agents: SupportAgents,
    operations: OperationService,
    conversation: Conversation,
    claim: Claim,
    operation: Operation,
    principal: Principal,
) -> None:
    """Resumes the paused specialist with the customer's decision, as a new turn; always completes the claim."""
    started = now()
    text = await resume(agents, operations, conversation, operation, principal)
    turn = Turn(
        conversation.conversation_id,
        claim.turn_id,
        None,
        TurnRoute(target=operation.specialist),
        "approval_result",
        text,
        claim.trace_id,
        started,
        now(),
        "completed",
        None,
        None,
        (operation.operation_id,),
        operation.source_turn_id,
    )
    conversation.complete(claim, turn)
    operation.decision_turn_id = operation.decision_turn_id or turn.turn_id


async def resume(
    agents: SupportAgents,
    operations: OperationService,
    conversation: Conversation,
    operation: Operation,
    principal: Principal,
) -> str:
    """Answers the native request in the conversation's own session. Approve: the tool may run, inside an approval scope
    naming exactly this operation. Reject: the tool never runs. Any new request in this run is declined (bounded)."""
    native = operations.pending_requests.pop(operation.operation_id, None)
    if native is None:
        return factual(operation)  # nothing is paused any more: nothing runs
    approved = operation.status is Status.RUNNING
    agent = agents.specialist(operation.specialist)
    session = conversation.sessions.for_agent(operation.specialist, agent)
    try:
        with pinned(principal):
            if approved:
                with approval_scope(operation):
                    response: AgentResponse[Any] = await agent.run(answer([native], approved=True), session=session)
                    response = await decline_follow_ups(agent, session, response)
            else:
                response = await decline(agent, session, [native])
                response = await decline_follow_ups(agent, session, response)
    except Exception:
        log.exception("Resuming operation %s failed", operation.operation_id)
        if operation.status is Status.RUNNING:
            operation.status, operation.failure, operation.updated_at = (
                Status.FAILED,
                ("internal_error", "The approved action could not be completed."),
                datetime.now(UTC),
            )
        return factual(operation)
    if operation.status is Status.COMMITTED:
        return response.text.strip() or factual(operation)
    if operation.status is Status.RUNNING:  # approved, but the model never ran the tool
        operation.status, operation.failure, operation.updated_at = (
            Status.FAILED,
            ("internal_error", "The approved action was not carried out."),
            datetime.now(UTC),
        )
    return factual(operation)


async def decline_follow_ups(agent: Any, session: Any, response: AgentResponse[Any]) -> AgentResponse[Any]:
    """One decision resumes one write, never a chain: any new approval request in this run is declined (bounded)."""
    for _ in range(3):
        pending = requests(response)
        if not pending:
            break
        response = await decline(agent, session, pending)
    return response


__all__ = ["FALLBACK", "create_router"]
