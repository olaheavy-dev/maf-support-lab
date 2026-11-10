"""Conversations and their turns (L18): owned by one customer, one turn at a time, each client_turn_id run once.

In memory for now: isolated sessions arrive in L19 and durable state in L38. Behaviour follows the version-1
contract: a repeated client_turn_id returns the original turn and never runs it again; reusing one with a different
message is a conflict; a different turn while one runs is rejected (turn_in_progress), never queued.
"""

from __future__ import annotations

import asyncio
import secrets
from dataclasses import dataclass, field
from datetime import UTC, datetime
from enum import StrEnum
from typing import Any

from support_agent.identity import Principal
from support_agent.operations import OperationStore, Status
from support_agent.routing import TurnRoute
from support_agent.sessions import ConversationContext, ConversationSessions


def new_id(prefix: str) -> str:
    return prefix + secrets.token_hex(8)


def now() -> datetime:
    return datetime.now(UTC)


@dataclass(frozen=True, slots=True)
class TurnError:
    code: str
    detail: str


@dataclass(frozen=True, slots=True)
class Turn:
    conversation_id: str
    turn_id: str
    client_turn_id: str | None  # None for a turn that resumed after an approval decision
    route: TurnRoute | None
    reply_kind: str | None
    reply_text: str | None
    trace_id: str
    created_at: datetime
    finished_at: datetime
    outcome: str = "completed"
    error: TurnError | None = None
    pending_operation_id: str | None = None
    operation_ids: tuple[str, ...] = ()
    resumed_from_turn_id: str | None = None

    def to_contract(self, contract_version: str, operations: OperationStore) -> dict[str, Any]:
        """The turn with each operation's *current* status (the authoritative record)."""
        touched = [op for op in (operations.find(op_id) for op_id in self.operation_ids) if op is not None]
        body: dict[str, Any] = {
            "contract_version": contract_version,
            "conversation_id": self.conversation_id,
            "turn_id": self.turn_id,
            "client_turn_id": self.client_turn_id,
            "state": "finished",
            "outcome": self.outcome,
            "operations": [op.summary_contract() for op in touched],
            "trace_id": self.trace_id,
            "created_at": _timestamp(self.created_at),
            "finished_at": _timestamp(self.finished_at),
        }
        if self.route is not None:
            body["route"] = self.route.to_contract()
        if self.outcome != "failed" and self.reply_kind is not None:
            body["reply"] = {"kind": self.reply_kind, "text": self.reply_text}
        if self.error is not None:
            body["error"] = {"code": self.error.code, "detail": self.error.detail}
        if self.resumed_from_turn_id is not None:
            body["resumed_from_turn_id"] = self.resumed_from_turn_id
        if (
            self.outcome == "awaiting_approval"
            and self.pending_operation_id
            and (pending := operations.find(self.pending_operation_id))
        ):
            body["pending_approval"] = pending.pending_approval()
        return body


def _timestamp(value: datetime) -> str:
    return value.astimezone(UTC).isoformat().replace("+00:00", "Z")


class Reservation(StrEnum):
    OWNER = "owner"
    DUPLICATE = "duplicate"
    BUSY = "busy"


@dataclass
class Claim:
    turn_id: str
    message: str
    done: asyncio.Future[Turn]
    client_turn_id: str | None = None
    started_at: datetime = field(default_factory=now)
    trace_id: str = ""

    def running_contract(self, conversation_id: str, contract_version: str) -> dict[str, Any]:
        """A turn still running: what a status lookup can say before it finishes (no outcome yet)."""
        return {
            "contract_version": contract_version,
            "conversation_id": conversation_id,
            "turn_id": self.turn_id,
            "client_turn_id": self.client_turn_id,
            "state": "running",
            "trace_id": self.trace_id,
            "created_at": _timestamp(self.started_at),
        }


@dataclass
class Conversation:
    conversation_id: str
    owner: str
    created_at: datetime
    claims: dict[str, Claim] = field(default_factory=dict[str, Claim])
    turns: dict[str, Turn] = field(default_factory=dict[str, Turn])
    active_turn_id: str | None = None
    # This conversation's own state (L19): verified context and native sessions. Never shared, never global.
    context: ConversationContext = field(default_factory=ConversationContext)
    sessions: ConversationSessions = field(default_factory=ConversationSessions)

    def is_owned_by(self, principal: Principal) -> bool:
        return self.owner == principal.customer_id

    def reserve(self, client_turn_id: str, message: str) -> tuple[Claim | None, Reservation]:
        """Claims a client_turn_id before any work starts (no awaits, so it is atomic on the event loop)."""
        if (existing := self.claims.get(client_turn_id.lower())) is not None:
            return existing, Reservation.DUPLICATE
        if self.active_turn_id is not None:
            return None, Reservation.BUSY
        claim = Claim(
            new_id("turn_"),
            message,
            asyncio.get_running_loop().create_future(),
            None
            if client_turn_id.startswith("approval:")
            else client_turn_id,  # a decision's turn has no client_turn_id
            now(),
            secrets.token_hex(16),
        )
        self.claims[client_turn_id.lower()] = claim
        self.active_turn_id = claim.turn_id
        return claim, Reservation.OWNER

    def complete(self, claim: Claim, turn: Turn, verified_order_id: str | None = None) -> None:
        self.turns[turn.turn_id] = turn
        if turn.outcome == "completed":
            self.context = self.context.after(turn.route.target if turn.route else None, verified_order_id)
        if self.active_turn_id == claim.turn_id:
            self.active_turn_id = None
        if not claim.done.done():
            claim.done.set_result(turn)

    def running(self, turn_id: str) -> Claim | None:
        """The claim for this turn if it is the one running now."""
        if self.active_turn_id != turn_id:
            return None
        return next((claim for claim in self.claims.values() if claim.turn_id == turn_id), None)

    def abandon(self, key: str, claim: Claim) -> None:
        """Releases a claim that never produced a turn (for example a decision that could not be recorded)."""
        if self.claims.get(key.lower()) is claim:
            del self.claims[key.lower()]
        if self.active_turn_id == claim.turn_id:
            self.active_turn_id = None

    def to_contract(self, contract_version: str, backend: str, operations: OperationStore) -> dict[str, Any]:
        """The conversation with its approvals still awaiting a decision (expired ones are marked and left out)."""
        at = datetime.now(UTC)
        pending = [
            op.pending_approval()
            for op in operations.for_conversation(self.conversation_id)
            if not op.expire_if_due(at) and op.status is Status.AWAITING_APPROVAL and op.source_turn_id in self.turns
        ]
        return {
            "contract_version": contract_version,
            "conversation_id": self.conversation_id,
            "backend": backend,
            "created_at": _timestamp(self.created_at),
            "active_turn_id": self.active_turn_id,
            "pending_approvals": pending,
        }


class ConversationStore:
    def __init__(self) -> None:
        self._conversations: dict[str, Conversation] = {}

    def start(self, principal: Principal) -> Conversation:
        conversation = Conversation(new_id("conv_"), principal.customer_id, now())
        self._conversations[conversation.conversation_id] = conversation
        return conversation

    def find_owned(self, conversation_id: str, principal: Principal) -> Conversation | None:
        """Only for its owner: anyone else gets None, reported exactly like a conversation that does not exist."""
        conversation = self._conversations.get(conversation_id)
        return conversation if conversation is not None and conversation.is_owned_by(principal) else None
