"""Conversations and their turns (L18): owned by one customer, one turn at a time, each client_turn_id run once.

Behaviour follows the version-1 contract: a repeated client_turn_id returns the original turn and never runs it again;
reusing one with a different message is a conflict; a different turn while one runs is rejected (turn_in_progress),
never queued. Durable since L38: conversations, finished turns, and their context are stored in SQLite. The first
stored turn for a client key wins, and a turn is stored together with the context it produced, or not at all. Which
turn is *running* is in-process state: after a restart, nothing is running.
"""

from __future__ import annotations

import asyncio
import json
import secrets
from dataclasses import dataclass, field
from datetime import UTC, datetime
from enum import StrEnum
from typing import Any

from support_agent.database import Database
from support_agent.identity import Principal
from support_agent.operations import OperationStore, Status
from support_agent.routing import TurnRoute, UnroutedReason
from support_agent.sessions import ConversationContext, ConversationSessions
from support_agent.telemetry import current_trace_id
from support_agent.turn_control import TurnControl


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

    def to_json(self) -> str:
        return json.dumps(
            {
                "conversation_id": self.conversation_id,
                "turn_id": self.turn_id,
                "client_turn_id": self.client_turn_id,
                "route": None
                if self.route is None
                else {"target": self.route.target, "unrouted_reason": self.route.unrouted_reason},
                "reply_kind": self.reply_kind,
                "reply_text": self.reply_text,
                "trace_id": self.trace_id,
                "created_at": self.created_at.isoformat(),
                "finished_at": self.finished_at.isoformat(),
                "outcome": self.outcome,
                "error": None if self.error is None else {"code": self.error.code, "detail": self.error.detail},
                "pending_operation_id": self.pending_operation_id,
                "operation_ids": list(self.operation_ids),
                "resumed_from_turn_id": self.resumed_from_turn_id,
            }
        )

    @classmethod
    def from_json(cls, text: str) -> Turn:
        data: dict[str, Any] = json.loads(text)
        route: dict[str, Any] | None = data["route"]
        error: dict[str, str] | None = data["error"]
        return cls(
            data["conversation_id"],
            data["turn_id"],
            data["client_turn_id"],
            None
            if route is None
            else TurnRoute(
                route["target"], UnroutedReason(route["unrouted_reason"]) if route["unrouted_reason"] else None
            ),
            data["reply_kind"],
            data["reply_text"],
            data["trace_id"],
            datetime.fromisoformat(data["created_at"]),
            datetime.fromisoformat(data["finished_at"]),
            data["outcome"],
            None if error is None else TurnError(error["code"], error["detail"]),
            data["pending_operation_id"],
            tuple(data["operation_ids"]),
            data["resumed_from_turn_id"],
        )

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
    key: str = ""  # the client_turn_id, or "approval:<id>" for a decision (lower case)
    control: TurnControl = field(default_factory=TurnControl)  # cancellation and live events (L20)

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
    database: Database | None = None

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
            current_trace_id() or secrets.token_hex(16),  # the request's trace (L40); random outside any trace
            client_turn_id.lower(),
        )
        self.claims[client_turn_id.lower()] = claim
        self.active_turn_id = claim.turn_id
        return claim, Reservation.OWNER

    def complete(
        self, claim: Claim, turn: Turn, operations: OperationStore, verified_order_id: str | None = None
    ) -> Turn:
        """Stores the finished turn and the context it produced, then answers everyone waiting on the claim.

        The first stored turn for a client key wins: if another run (for example on a host that restarted while this
        one was stuck) stored one first, that stored turn is the answer. If storing fails, the claim is released so a
        retry can run, and the waiters get the error.
        """
        try:
            self._withdraw_unanswerable(turn, operations)
            context = (
                self.context.after(turn.route.target if turn.route else None, verified_order_id)
                if turn.outcome == "completed"
                else self.context
            )
            stored, context = self._store(claim, turn, context)
        except BaseException as error:
            self.abandon(claim.key, claim)
            if not claim.done.done():
                claim.done.set_exception(error)
                claim.done.exception()  # retrieved: a turn nobody waits for must not log "never retrieved"
            raise
        self.turns[stored.turn_id] = stored
        self.context = context
        if self.active_turn_id == claim.turn_id:
            self.active_turn_id = None
        if not claim.done.done():
            claim.done.set_result(stored)
        return stored

    def _withdraw_unanswerable(self, turn: Turn, operations: OperationStore) -> None:
        """Approvals the customer can never answer expire: those of a failed or cancelled turn, and those whose turn was never
        stored (the process stopped between proposing and storing the turn). Only one turn runs at a time, so any
        other unstored proposal is an orphan."""
        at = now()
        for operation in operations.for_conversation(self.conversation_id):
            if operation.status is not Status.AWAITING_APPROVAL or operation.source_turn_id in self.turns:
                continue
            if operation.source_turn_id != turn.turn_id or turn.outcome in ("failed", "cancelled"):
                operations.expire(operation, at)

    def _store(self, claim: Claim, turn: Turn, context: ConversationContext) -> tuple[Turn, ConversationContext]:
        if self.database is None:
            return turn, context
        with self.database.atomically():
            inserted = self.database.execute(
                "INSERT INTO turns(turn_id, conversation_id, client_key, message, json) VALUES (?, ?, ?, ?, ?) "
                "ON CONFLICT DO NOTHING",
                (turn.turn_id, self.conversation_id, claim.key, claim.message, turn.to_json()),
            )
            if inserted:
                self.database.execute(
                    "UPDATE conversations SET context_json = ? WHERE conversation_id = ?",
                    (context.to_json(), self.conversation_id),
                )
                return turn, context
            stored = self.database.query(
                "SELECT json FROM turns WHERE conversation_id = ? AND client_key = ?", (self.conversation_id, claim.key)
            )
            current = self.database.query(
                "SELECT context_json FROM conversations WHERE conversation_id = ?", (self.conversation_id,)
            )
            return Turn.from_json(stored[0][0]), ConversationContext.from_json(current[0][0])

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
            if not operations.expire_if_due(op, at)
            and op.status is Status.AWAITING_APPROVAL
            and op.source_turn_id in self.turns
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
    """Owned conversations, kept in memory once loaded, and stored in the database (L38)."""

    def __init__(self, database: Database) -> None:
        self._database = database
        self._conversations: dict[str, Conversation] = {}

    def start(self, principal: Principal) -> Conversation:
        conversation = self._new(new_id("conv_"), principal.customer_id, now(), ConversationContext())
        self._database.execute(
            "INSERT INTO conversations(conversation_id, owner, created_at, context_json) VALUES (?, ?, ?, ?)",
            (
                conversation.conversation_id,
                conversation.owner,
                conversation.created_at.isoformat(),
                conversation.context.to_json(),
            ),
        )
        self._conversations[conversation.conversation_id] = conversation
        return conversation

    def find_owned(self, conversation_id: str, principal: Principal) -> Conversation | None:
        """Only for its owner: anyone else gets None, reported exactly like a conversation that does not exist."""
        conversation = self._conversations.get(conversation_id) or self._load(conversation_id)
        return conversation if conversation is not None and conversation.is_owned_by(principal) else None

    def _new(
        self, conversation_id: str, owner: str, created_at: datetime, context: ConversationContext
    ) -> Conversation:
        return Conversation(
            conversation_id,
            owner,
            created_at,
            context=context,
            sessions=ConversationSessions(conversation_id, self._database),
            database=self._database,
        )

    def _load(self, conversation_id: str) -> Conversation | None:
        """A conversation stored before this process started: its finished turns replay; nothing is running."""
        rows = self._database.query(
            "SELECT owner, created_at, context_json FROM conversations WHERE conversation_id = ?", (conversation_id,)
        )
        if not rows:
            return None
        owner, created_at, context_json = rows[0]
        conversation = self._new(
            conversation_id, owner, datetime.fromisoformat(created_at), ConversationContext.from_json(context_json)
        )
        loop = asyncio.get_running_loop()
        for key, message, text in self._database.query(
            "SELECT client_key, message, json FROM turns WHERE conversation_id = ?", (conversation_id,)
        ):
            turn = Turn.from_json(text)
            conversation.turns[turn.turn_id] = turn
            done: asyncio.Future[Turn] = loop.create_future()
            done.set_result(turn)
            conversation.claims[key] = Claim(
                turn.turn_id, message, done, turn.client_turn_id, turn.created_at, turn.trace_id, key
            )
        self._conversations[conversation_id] = conversation
        return conversation
