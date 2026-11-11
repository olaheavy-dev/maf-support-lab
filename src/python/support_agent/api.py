"""The version-1 contract API (L18; approvals and operations since L37; streaming and cancellation since L20).

Every request body is validated against the shared contract schema before anything runs. Another customer's
conversation, turn, approval, or operation is 404, exactly like a missing one. A turn that runs and fails is a result
(200, outcome failed). Each turn runs as its own task with the caller's principal pinned: a dropped request never
stops it, and only ``POST …/turns/{id}/cancel`` does (cooperatively). ``Accept: text/event-stream`` on the turn and
approval routes streams the turn as Server-Sent Events. Approval decisions are bound to the customer, the
conversation, the operation, and one decision; repeating the same decision returns the same turn and never runs the
write twice, also after a restart (L38).
"""

from __future__ import annotations

import asyncio
import json
import logging
import secrets
import time
from collections.abc import Callable, Coroutine, Generator
from contextlib import contextmanager
from datetime import UTC, datetime
from typing import Any

from agent_framework import AgentResponse, Message, SupportsAgentRun
from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse, Response
from opentelemetry.trace import StatusCode

from support_agent import contract, injection, streaming
from support_agent.agents import SupportAgents
from support_agent.approval_gate import answer, batch_of, decline, requests
from support_agent.conversations import Claim, Conversation, ConversationStore, Reservation, Turn, TurnError, now
from support_agent.dependencies import Caller
from support_agent.identity import Principal, pinned
from support_agent.operations import Decision, Operation, OperationService, Status, approval_scope
from support_agent.resilience import FailureCode, budgeting, classify, current_budget
from support_agent.routing import TurnRoute
from support_agent.settings import ResilienceSettings
from support_agent.telemetry import current_trace_id, tracer, turn_duration, turns
from support_agent.turn_control import Delta, Progress, TurnControl, controlling, emit, raise_if_cancelled
from support_agent.workflow import FALLBACK, RoutingRequest, SupportReply, build_workflow

log = logging.getLogger(__name__)

TITLES = {
    "invalid_request": "Invalid request",
    "not_found": "Not found",
    "client_turn_id_reused": "client_turn_id reused",
    "turn_in_progress": "Turn in progress",
    "turn_already_finished": "Turn already finished",
    "approval_mismatch": "Approval mismatch",
    "approval_already_decided": "Approval already decided",
    "approval_expired": "Approval expired",
}
STATUS = {
    "invalid_request": 400,
    "not_found": 404,
    "client_turn_id_reused": 409,
    "turn_in_progress": 409,
    "turn_already_finished": 409,
    "approval_mismatch": 409,
    "approval_already_decided": 409,
    "approval_expired": 410,
}
FAILED = TurnError("internal_error", "Something went wrong while handling your message. Please try again.")
TIMED_OUT_WRITE = "The approved action took too long and was stopped before anything was changed."
SHUTDOWN_GRACE_SECONDS = 5.0


def trace_id() -> str:
    """The current request's trace id (32 hex), so a problem response can be found in Aspire; random outside a trace."""
    return current_trace_id() or secrets.token_hex(16)


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


class RunningTurns:
    """The host's running turn tasks. Each runs detached from any request; shutdown cancels and awaits them, so every
    turn ends stored (as cancelled if it had to be stopped) before the database closes."""

    def __init__(self) -> None:
        self._tasks: dict[asyncio.Task[None], TurnControl] = {}

    def detach(self, work: Coroutine[Any, Any, None], control: TurnControl) -> None:
        task: asyncio.Task[None] = asyncio.create_task(work)
        control.task = task
        self._tasks[task] = control
        task.add_done_callback(self._finished)

    def _finished(self, task: asyncio.Task[None]) -> None:
        """The waiters already got the outcome (or the error, e.g. a turn that could not be stored); this only logs."""
        self._tasks.pop(task, None)
        if not task.cancelled() and (error := task.exception()) is not None:
            log.error("A detached turn ended with an error", exc_info=error)

    async def shutdown(self) -> None:
        for control in list(self._tasks.values()):
            control.request_cancel()
        if self._tasks:
            await asyncio.wait(list(self._tasks), timeout=SHUTDOWN_GRACE_SECONDS)

    def __len__(self) -> int:
        return len(self._tasks)


def create_router(
    store: ConversationStore,
    agents: SupportAgents,
    operations: OperationService,
    turns: RunningTurns,
    limits: ResilienceSettings | None = None,
) -> APIRouter:
    limits = limits or ResilienceSettings()
    router = APIRouter(prefix="/api/v1")
    ops = operations.store

    def turn_json(turn: Turn) -> JSONResponse:
        return JSONResponse(turn.to_contract(contract.CONTRACT_VERSION, ops))

    def final_of(conversation: Conversation, claim: Claim, resumed_from: str | None) -> Any:
        """The final result for a stream: the stored turn, or (if it could not be stored) a failed one."""

        def final(turn: Turn | None) -> dict[str, Any]:
            if turn is None:
                turn = failed_turn(conversation, claim, claim.started_at, resumed_from)
            return turn.to_contract(contract.CONTRACT_VERSION, ops)

        return final

    async def answer_claim(
        request: Request,
        conversation: Conversation,
        claim: Claim,
        live: bool,
        resumed_from: str | None = None,
    ) -> Response:
        if streaming.is_requested(request):
            started: dict[str, Any] = {"client_turn_id": claim.client_turn_id}
            if resumed_from is not None:
                started["resumed_from_turn_id"] = resumed_from
            return streaming.respond(
                claim, conversation.conversation_id, started, live, final_of(conversation, claim, resumed_from)
            )
        return turn_json(await asyncio.shield(claim.done))

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
    async def submit_turn(conversation_id: str, request: Request, principal: Caller) -> Response:
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
        owner = outcome is Reservation.OWNER
        if owner:
            if streaming.is_requested(request):
                claim.control.observer = asyncio.Queue()  # attached before the turn starts, so nothing is missed
            turns.detach(
                complete_turn(agents, operations, conversation, claim, message, principal, limits), claim.control
            )
        return await answer_claim(request, conversation, claim, live=owner)

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

    @router.post("/conversations/{conversation_id}/turns/{turn_id}/cancel", status_code=202)
    async def cancel_turn(conversation_id: str, turn_id: str, principal: Caller) -> JSONResponse:
        """Cooperative: asks the running turn to stop. Its final outcome reports what actually happened."""
        conversation = store.find_owned(conversation_id, principal)
        if conversation is None:
            return not_found()
        if turn_id in conversation.turns:
            return problem("turn_already_finished", "This turn has already finished.")
        running = conversation.running(turn_id)
        if running is None:
            return not_found()
        running.control.request_cancel()
        body = {"contract_version": contract.CONTRACT_VERSION, "turn_id": turn_id, "cancel_requested": True}
        return JSONResponse(body, status_code=202)

    @router.post("/conversations/{conversation_id}/approvals/{approval_id}")
    async def decide(conversation_id: str, approval_id: str, request: Request, principal: Caller) -> Response:
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
        if operation.decision is None and ops.expire_if_due(operation, datetime.now(UTC)):
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
            return await answer_claim(request, conversation, claim, live=False, resumed_from=operation.source_turn_id)
        try:
            # The first stored decision wins (guarded on the stored status). The same decision recorded earlier, by a
            # run whose turn was never stored (a crash), is finished from the record below.
            if operation.decision is None:
                ops.decide(operation, decision, body.get("reason"), datetime.now(UTC))
        except BaseException:
            conversation.abandon(key, claim)
            raise
        if operation.decision is not decision:
            conversation.abandon(key, claim)
            expired = operation.status is Status.EXPIRED
            return problem(
                "approval_expired" if expired else "approval_already_decided", "This approval can no longer be decided."
            )
        if streaming.is_requested(request):
            claim.control.observer = asyncio.Queue()
        turns.detach(
            complete_decision(agents, operations, conversation, claim, operation, principal, limits), claim.control
        )
        return await answer_claim(request, conversation, claim, live=True, resumed_from=operation.source_turn_id)

    @router.get("/operations/{operation_id}")
    async def get_operation(operation_id: str, principal: Caller) -> JSONResponse:
        operation = ops.find(operation_id)
        if operation is None or operation.owner != principal.customer_id:
            return not_found()
        ops.expire_if_due(operation, datetime.now(UTC))
        return JSONResponse(operation.to_contract(contract.CONTRACT_VERSION))

    return router


@contextmanager
def observed_turn(
    conversation: Conversation, claim: Claim, resumed_from: str | None = None
) -> Generator[Callable[[Turn], None]]:
    """The ``support.turn`` span around one turn's run (a child of its request's span, which the detached task
    inherits), plus the turn metrics and one structured log line. Ids go on the span and the log, never on metrics;
    no message text goes anywhere."""
    attributes: dict[str, Any] = {
        "support.conversation.id": conversation.conversation_id,
        "support.turn.id": claim.turn_id,
        "support.turn.resumed": resumed_from is not None,
    }
    if claim.client_turn_id is not None:
        attributes["support.client_turn.id"] = claim.client_turn_id
    clock = time.monotonic()
    with tracer.start_as_current_span("support.turn", attributes=attributes) as span:

        def observe(turn: Turn) -> None:
            agent = turn.route.target if turn.route and turn.route.target else ("unrouted" if turn.route else "unknown")
            labels = {"support.agent": agent, "support.turn.outcome": turn.outcome}
            span.set_attributes({**labels, "support.reply_kind": turn.reply_kind or "none"})
            if (budget := current_budget()) is not None:
                # L42: model calls (including one refused over budget) and HTTP attempts (including SDK retries).
                span.set_attributes(
                    {
                        "support.turn.model_calls": budget.model_calls,
                        "support.turn.model_attempts": budget.model_attempts,
                    }
                )
            turns.add(1, {**labels, "support.reply_kind": turn.reply_kind or "none"})
            turn_duration.record(time.monotonic() - clock, labels)
            if turn.outcome == "failed":
                code = turn.error.code if turn.error else "internal_error"
                span.set_status(StatusCode.ERROR, code)
                span.set_attributes({"error.type": code, "support.turn.error_code": code})
                log.error(
                    "Turn failed",
                    extra={
                        "support.turn.id": turn.turn_id,
                        "support.conversation.id": turn.conversation_id,
                        "support.turn.error_code": code,
                        **labels,
                    },
                )
            else:
                log.info(
                    "Turn finished",
                    extra={"support.turn.id": turn.turn_id, "support.conversation.id": turn.conversation_id, **labels},
                )

        yield observe


def acknowledge_cancellation() -> None:
    """The turn handled its cancellation (it stores a cancelled or factual result), so its task carries on normally."""
    if (task := asyncio.current_task()) is not None and task.cancelling():
        task.uncancel()


async def complete_turn(
    agents: SupportAgents,
    operations: OperationService,
    conversation: Conversation,
    claim: Claim,
    message: str,
    principal: Principal,
    limits: ResilienceSettings | None = None,
) -> None:
    """Runs the claimed turn to its stored result; whatever happens, the claim is completed exactly once. The turn has
    its own model-call budget and deadline (L42)."""
    limits = limits or ResilienceSettings()
    with budgeting(limits.max_model_calls_per_turn), observed_turn(conversation, claim) as observe:
        injection.inspect("message", message)  # diagnostic only (L41): the turn runs exactly the same either way
        await _complete_turn(agents, operations, conversation, claim, message, principal, observe, limits)


async def _complete_turn(
    agents: SupportAgents,
    operations: OperationService,
    conversation: Conversation,
    claim: Claim,
    message: str,
    principal: Principal,
    observe: Callable[[Turn], None],
    limits: ResilienceSettings,
) -> None:
    deadline = asyncio.timeout(limits.turn_timeout_seconds)
    try:
        with controlling(claim.control):
            claim.control.started = True
            raise_if_cancelled()  # cancelled before this task first ran: stored as cancelled below
            async with deadline:
                turn, verified_order_id = await run_turn(agents, operations, conversation, claim, message, principal)
    except asyncio.CancelledError:
        acknowledge_cancellation()
        observe(conversation.complete(claim, cancelled_turn(conversation, claim, claim.started_at), operations.store))
        return
    except TimeoutError:
        if not deadline.expired():
            raise
        # The deadline stopped the turn at its next await. A normal turn never writes (writes run only after an
        # approval), so nothing changed.
        log.warning("Turn %s passed its deadline", claim.turn_id)
        timed_out = failed_turn(conversation, claim, claim.started_at, error=turn_error(classify_timeout()))
        observe(conversation.complete(claim, timed_out, operations.store))
        return
    except BaseException:
        observe(conversation.complete(claim, failed_turn(conversation, claim, claim.started_at), operations.store))
        raise
    observe(conversation.complete(claim, turn, operations.store, verified_order_id))


def turn_error(failure: FailureCode) -> TurnError:
    return TurnError(failure.code, failure.detail, failure.retry_after_seconds)


def classify_timeout() -> FailureCode:
    return classify(TimeoutError(), turn_timed_out=True)


def failed_turn(
    conversation: Conversation,
    claim: Claim,
    started: datetime,
    resumed_from: str | None = None,
    error: TurnError = FAILED,
) -> Turn:
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
        error,
        resumed_from_turn_id=resumed_from,
    )


def cancelled_turn(
    conversation: Conversation,
    claim: Claim,
    started: datetime,
    resumed_from: str | None = None,
    operation_ids: tuple[str, ...] = (),
    route: TurnRoute | None = None,
) -> Turn:
    """Stopped on request before doing anything further: no reply, and nothing changed."""
    return Turn(
        conversation.conversation_id,
        claim.turn_id,
        claim.client_turn_id,
        route,
        None,
        None,
        claim.trace_id,
        started,
        now(),
        "cancelled",
        operation_ids=operation_ids,
        resumed_from_turn_id=resumed_from,
    )


async def run_turn(
    agents: SupportAgents,
    operations: OperationService,
    conversation: Conversation,
    claim: Claim,
    message: str,
    principal: Principal,
) -> tuple[Turn, str | None]:
    """Runs the workflow as the caller (pinned) in this conversation's own sessions and context.

    The workflow is read as a native stream: its progress and reply-text events go to the turn's control (and to a
    streaming client, if one is attached). Cancellation propagates (``CancelledError``); other failures are results."""
    started = now()
    reply: SupportReply | None = None
    with pinned(principal):
        try:
            request = RoutingRequest(
                message, conversation.conversation_id, conversation.context, conversation.sessions, claim.turn_id
            )
            async for event in build_workflow(agents, operations).run(request, stream=True):
                if event.type == "data" and isinstance(event.data, Progress | Delta):
                    emit(event.data)
                elif event.type == "output" and isinstance(event.data, SupportReply):
                    reply = event.data
            if reply is None:
                raise RuntimeError("The workflow finished without a reply.")
        except Exception as error:
            # Classified (L42): the code tells the client what happened; the detail is fixed, safe text. The full error
            # goes to the log and the trace, never into the result.
            failure = classify(error)
            log.exception(
                "Turn %s failed in conversation %s (%s)", claim.turn_id, conversation.conversation_id, failure.code
            )
            return failed_turn(conversation, claim, started, error=turn_error(failure)), None
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


def committed(operation: Operation) -> bool:
    return operation.status is Status.COMMITTED


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
    limits: ResilienceSettings | None = None,
) -> None:
    """Resumes the paused specialist with the customer's decision, as a new turn; always completes the claim.

    Cancelled before the write commits: the turn is cancelled, and an approved operation fails with code ``cancelled``.
    Cancelled after it committed: the committed result stands, and the turn completes from the record.
    Bounded like any turn (L42): past its deadline before the commit, the operation fails with code ``timeout`` and
    nothing changed; past it after the commit, the committed result stands."""
    limits = limits or ResilienceSettings()
    with (
        budgeting(limits.max_model_calls_per_turn),
        observed_turn(conversation, claim, resumed_from=operation.source_turn_id) as observe,
    ):
        # Diagnostic only. In Python the reason never reaches the model (MAF's approval response carries none, L37).
        injection.inspect("decision_reason", operation.decision_reason)
        await _complete_decision(agents, operations, conversation, claim, operation, principal, observe, limits)


async def _complete_decision(
    agents: SupportAgents,
    operations: OperationService,
    conversation: Conversation,
    claim: Claim,
    operation: Operation,
    principal: Principal,
    observe: Callable[[Turn], None],
    limits: ResilienceSettings,
) -> None:
    started = now()
    route = TurnRoute(target=operation.specialist)
    deadline = asyncio.timeout(limits.turn_timeout_seconds)
    try:
        with controlling(claim.control):
            claim.control.started = True
            raise_if_cancelled()
            async with deadline:
                text = await resume(agents, operations, conversation, operation, principal)
    except TimeoutError:
        if not deadline.expired():
            raise
        # Guarded on the stored status: an approved write still running changed nothing (its write and its commit are
        # one transaction), so it fails with code timeout. One that committed just before the deadline stays committed,
        # and the turn reports it. A timeout never rolls a write back.
        log.warning("Resuming operation %s passed its deadline", operation.operation_id)
        operations.store.fail(operation, TIMED_OUT_WRITE, datetime.now(UTC), code="timeout")
        text = factual(operation)
    except asyncio.CancelledError:
        acknowledge_cancellation()
        operations.store.fail(operation, "Cancelled before the change was made.", datetime.now(UTC), code="cancelled")
        if not committed(operation):
            turn = cancelled_turn(
                conversation, claim, started, operation.source_turn_id, (operation.operation_id,), route
            )
            observe(conversation.complete(claim, turn, operations.store))
            return
        text = factual(operation)
    except BaseException as error:
        conversation.abandon(claim.key, claim)
        if not claim.done.done():
            claim.done.set_exception(error)
        raise
    turn = Turn(
        conversation.conversation_id,
        claim.turn_id,
        None,
        route,
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
    observe(conversation.complete(claim, turn, operations.store))


async def streamed_run(agent: SupportsAgentRun, messages: list[Message], session: Any) -> AgentResponse[Any]:
    """Runs the agent as a native stream, sending its text and tool calls to the turn's live events. Closed
    deterministically, also when cancelled mid-stream."""
    async with agent.run(messages, session=session, stream=True) as stream:
        async for update in stream:
            for content in update.contents:
                if content.type == "function_call" and content.name:
                    emit(Progress("tool_call", tool=content.name))
            if update.text:
                emit(Delta(update.text))
        response: AgentResponse[Any] = await stream.get_final_response()
        return response


async def resume(
    agents: SupportAgents,
    operations: OperationService,
    conversation: Conversation,
    operation: Operation,
    principal: Principal,
) -> str:
    """Answers the native request in the conversation's own session. Approve: the tool may run, inside an approval scope
    naming exactly this operation. Reject: the tool never runs. Any new request in this run is declined (bounded).

    Only a paused request whose decision has not been carried out is resumed. Anything already finished (for example
    a write committed just before a crash) is reported from the stored record, without a model call."""
    ops = operations.store
    paused = ops.native_request(operation.operation_id)
    if paused is None:
        # Nothing is paused any more. An approval that can no longer run fails instead of staying "running".
        ops.fail(operation, "The approved action could not be resumed.", datetime.now(UTC))
        return factual(operation)
    native, batch = paused
    if operation.status not in (Status.RUNNING, Status.REJECTED):
        ops.answered(operation.operation_id)
        return factual(operation)
    approved = operation.status is Status.RUNNING
    agent = agents.specialist(operation.specialist)
    session = conversation.sessions.for_agent(operation.specialist, agent)
    try:
        with pinned(principal):
            if approved:
                emit(Progress("executing_operation", route={"status": "routed", "target": operation.specialist}))
                with approval_scope(operation):
                    response = await streamed_run(agent, answer([native], approved=True, batch=batch), session)
                    response = await decline_follow_ups(agent, session, response)
            else:
                response = await decline(agent, session, [native], batch)
                response = await decline_follow_ups(agent, session, response)
        ops.answered(operation.operation_id)
        conversation.sessions.save(operation.specialist)
    except Exception:
        log.exception("Resuming operation %s failed", operation.operation_id)
        ops.fail(operation, "The approved action could not be completed.", datetime.now(UTC))
        return factual(operation)
    if committed(operation):  # the tool ran inside the approval scope and committed the write
        return response.text.strip() or factual(operation)
    # Approved, but the model never ran the tool: record it, and say so from the record.
    ops.fail(operation, "The approved action was not carried out.", datetime.now(UTC))
    return factual(operation)


async def decline_follow_ups(agent: Any, session: Any, response: AgentResponse[Any]) -> AgentResponse[Any]:
    """One decision resumes one write, never a chain: any new approval request in this run is declined (bounded)."""
    for _ in range(3):
        pending = requests(response)
        if not pending:
            break
        response = await decline(agent, session, pending, batch_of(response, pending))
    return response


__all__ = ["FALLBACK", "RunningTurns", "create_router"]
