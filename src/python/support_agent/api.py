"""The version-1 contract API (L18): conversations and turns, JSON only (streaming is a later Python ticket).

Every request body is validated against the shared contract schema before anything runs (unknown fields such as
customer_id are rejected). Another customer's conversation or turn is 404, exactly like a missing one. A turn that runs
and fails is a result (200, outcome failed), not an HTTP error. The principal comes only from get_principal and is
pinned for the turn's work, so tools never read identity from anywhere else.
"""

from __future__ import annotations

import json
import logging
import secrets
from typing import Any

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse

from support_agent import contract
from support_agent.agents import SupportAgents
from support_agent.conversations import Conversation, ConversationStore, Reservation, Turn, TurnError, now
from support_agent.dependencies import Caller
from support_agent.identity import Principal, pinned
from support_agent.workflow import RoutingRequest, SupportReply, build_workflow

log = logging.getLogger(__name__)

TITLES = {
    "invalid_request": "Invalid request",
    "not_found": "Not found",
    "client_turn_id_reused": "client_turn_id reused",
    "turn_in_progress": "Turn in progress",
}
STATUS = {"invalid_request": 400, "not_found": 404, "client_turn_id_reused": 409, "turn_in_progress": 409}


def trace_id() -> str:
    """The turn's trace id (32 hex). Python OpenTelemetry arrives later (L40); until then it is generated here."""
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


def create_router(store: ConversationStore, agents: SupportAgents) -> APIRouter:
    router = APIRouter(prefix="/api/v1/conversations")

    @router.post("", status_code=201)
    async def create_conversation(request: Request, principal: Caller) -> JSONResponse:
        _, invalid = await read_body(request, "create-conversation-request")
        if invalid is not None:
            return invalid
        conversation = store.start(principal)
        return JSONResponse(conversation.to_contract(contract.CONTRACT_VERSION, contract.BACKEND), status_code=201)

    @router.get("/{conversation_id}")
    async def get_conversation(conversation_id: str, principal: Caller) -> JSONResponse:
        conversation = store.find_owned(conversation_id, principal)
        if conversation is None:
            return not_found()
        return JSONResponse(conversation.to_contract(contract.CONTRACT_VERSION, contract.BACKEND))

    @router.post("/{conversation_id}/turns")
    async def submit_turn(conversation_id: str, request: Request, principal: Caller) -> JSONResponse:
        # A conversation you can't see is 404 before anything else, so the body's validity reveals nothing about it.
        conversation = store.find_owned(conversation_id, principal)
        if conversation is None:
            return not_found()
        body, invalid = await read_body(request, "turn-request")
        if invalid is not None:
            return invalid
        client_turn_id: str = body["client_turn_id"]
        message: str = body["message"]

        claim, outcome = conversation.reserve(client_turn_id, message)
        if outcome is Reservation.BUSY or claim is None:
            return problem("turn_in_progress", "Another turn is still running in this conversation.")
        if claim.message != message:
            return problem("client_turn_id_reused", "This client_turn_id was already used with a different message.")
        if outcome is Reservation.DUPLICATE:
            return JSONResponse((await claim.done).to_contract(contract.CONTRACT_VERSION))

        turn = await run_turn(agents, conversation, claim.turn_id, client_turn_id, message, principal)
        conversation.complete(claim, turn)
        return JSONResponse(turn.to_contract(contract.CONTRACT_VERSION))

    @router.get("/{conversation_id}/turns/{turn_id}")
    async def get_turn(conversation_id: str, turn_id: str, principal: Caller) -> JSONResponse:
        conversation = store.find_owned(conversation_id, principal)
        turn = None if conversation is None else conversation.turns.get(turn_id)
        if turn is None:
            return not_found()
        return JSONResponse(turn.to_contract(contract.CONTRACT_VERSION))

    return router


async def run_turn(
    agents: SupportAgents,
    conversation: Conversation,
    turn_id: str,
    client_turn_id: str,
    message: str,
    principal: Principal,
) -> Turn:
    """Runs the workflow as the caller (pinned) and returns the finished turn: completed, or failed with a safe code."""
    started = now()
    with pinned(principal):
        try:
            result = await build_workflow(agents).run(RoutingRequest(message, conversation.conversation_id))
            reply: SupportReply = result.get_outputs()[0]
            return Turn(
                conversation.conversation_id,
                turn_id,
                client_turn_id,
                reply.route,
                reply.kind,
                reply.text,
                trace_id(),
                started,
                now(),
            )
        except Exception:
            log.exception("Turn %s failed in conversation %s", turn_id, conversation.conversation_id)
            failure = TurnError("internal_error", "Something went wrong while handling your message. Please try again.")
            return Turn(
                conversation.conversation_id,
                turn_id,
                client_turn_id,
                None,
                None,
                None,
                trace_id(),
                started,
                now(),
                "failed",
                failure,
            )
