"""Writes and their approvals (L37, porting L10).

A write is proposed as an ``Operation`` bound, when created, to the customer, the conversation, the kind, and the
exact arguments (canonical JSON). The customer's decision is recorded once; an approved write runs at most once, and
only inside an ``approval_scope`` naming exactly that operation. Eligibility and ownership are checked by the module
that owns the data, both before asking (so customers aren't asked to approve impossible actions) and again,
authoritatively, at execution. Approval is human consent; these checks are business authorization. Both are needed.
Durable since L38: operations and their paused native requests are stored in SQLite, every status change is guarded
on the stored status, and an approved write commits in one transaction with its "committed" record.
"""

from __future__ import annotations

import json
import re
import secrets
from collections.abc import Awaitable, Callable, Generator, Mapping, Sequence
from contextlib import contextmanager
from contextvars import ContextVar
from dataclasses import asdict, dataclass
from datetime import UTC, datetime, timedelta
from enum import StrEnum
from typing import Any

from agent_framework import Content

from support_agent.database import Database, Rollback
from support_agent.domain import Order, Payment
from support_agent.identity import Principal
from support_agent.policy import Policy
from support_agent.state import SimulatedState, Ticket
from support_agent.telemetry import operations as operation_metrics
from support_agent.turn_control import raise_if_cancelled

ORDER_ID = re.compile(r"^ORD-[0-9]{4}$")
MAX_SUMMARY = 500


class Kind(StrEnum):
    CANCEL_ORDER = "cancel_order"
    REFUND_ORDER = "refund_order"
    CREATE_TICKET = "create_ticket"


class Status(StrEnum):
    AWAITING_APPROVAL = "awaiting_approval"
    REJECTED = "rejected"
    EXPIRED = "expired"
    RUNNING = "running"
    COMMITTED = "committed"
    FAILED = "failed"


class Decision(StrEnum):
    APPROVE = "approve"
    REJECT = "reject"


class WriteError(Exception):
    """Why a write cannot happen. ``code`` matches the contract's failure codes where they overlap."""

    def __init__(self, code: str, detail: str) -> None:
        super().__init__(detail)
        self.code = code
        self.detail = detail


def not_found() -> WriteError:
    return WriteError("not_found", "The order does not exist or is not available to you.")


def canonical(arguments: Mapping[str, str]) -> str:
    """Two argument sets are the same action only if their canonical JSON (keys sorted) is identical."""
    return json.dumps(dict(arguments), sort_keys=True, separators=(",", ":"))


def now() -> datetime:
    return datetime.now(UTC)


@dataclass
class Operation:
    operation_id: str
    approval_id: str
    conversation_id: str
    owner: str
    kind: Kind
    arguments: dict[str, str]
    summary: str
    specialist: str
    source_turn_id: str
    created_at: datetime
    expires_at: datetime
    updated_at: datetime
    status: Status = Status.AWAITING_APPROVAL
    decision: Decision | None = None
    decision_turn_id: str | None = None
    # The customer's optional reason, recorded for audit. (MAF Python's approval response carries no reason, and a
    # note next to a tool result is rejected by Anthropic, so it is not sent to the model.)
    decision_reason: str | None = None
    result: dict[str, Any] | None = None
    failure: tuple[str, str] | None = None

    def to_json(self) -> str:
        data: dict[str, Any] = asdict(self)
        for name in ("created_at", "expires_at", "updated_at"):
            data[name] = getattr(self, name).isoformat()
        return json.dumps(data)

    @classmethod
    def from_json(cls, text: str) -> Operation:
        data: dict[str, Any] = json.loads(text)
        for name in ("created_at", "expires_at", "updated_at"):
            data[name] = datetime.fromisoformat(data[name])
        data["kind"], data["status"] = Kind(data["kind"]), Status(data["status"])
        data["decision"] = Decision(data["decision"]) if data["decision"] else None
        data["failure"] = tuple(data["failure"]) if data["failure"] else None
        return cls(**data)

    def refresh_from(self, stored: Operation) -> None:
        """Takes the stored record's state (after losing a guarded update, or after another run finished it)."""
        for name, value in vars(stored).items():
            setattr(self, name, value)

    def expire_if_due(self, at: datetime) -> bool:
        if self.status is Status.AWAITING_APPROVAL and at >= self.expires_at:
            self.status, self.updated_at = Status.EXPIRED, at
        return self.status is Status.EXPIRED

    def decide(self, decision: Decision, at: datetime) -> bool:
        """Records the customer's decision exactly once."""
        if self.decision is not None or self.status is not Status.AWAITING_APPROVAL:
            return False
        self.decision = decision
        self.status = Status.RUNNING if decision is Decision.APPROVE else Status.REJECTED
        self.updated_at = at
        return True

    def to_contract(self, contract_version: str) -> dict[str, Any]:
        body: dict[str, Any] = {
            "contract_version": contract_version,
            "operation_id": self.operation_id,
            "conversation_id": self.conversation_id,
            "approval_id": self.approval_id,
            "kind": str(self.kind),
            "arguments": dict(self.arguments),
            "status": str(self.status),
            "created_at": _timestamp(self.created_at),
            "updated_at": _timestamp(self.updated_at),
        }
        if self.status is Status.AWAITING_APPROVAL:
            body["expires_at"] = _timestamp(self.expires_at)
        if self.status is Status.COMMITTED and self.result is not None:
            body["result"] = dict(self.result)
        if self.status is Status.FAILED and self.failure is not None:
            body["failure"] = {"code": self.failure[0], "detail": self.failure[1]}
        return body

    def pending_approval(self) -> dict[str, Any]:
        return {
            "approval_id": self.approval_id,
            "operation_id": self.operation_id,
            "kind": str(self.kind),
            "arguments": dict(self.arguments),
            "summary": self.summary,
            "expires_at": _timestamp(self.expires_at),
        }

    def summary_contract(self) -> dict[str, str]:
        return {"operation_id": self.operation_id, "kind": str(self.kind), "status": str(self.status)}


def _timestamp(value: datetime) -> str:
    return value.astimezone(UTC).isoformat().replace("+00:00", "Z")


class OperationStore:
    """The authoritative operation records. Every read is fresh from the database; every change is guarded."""

    def __init__(self, database: Database) -> None:
        self._database = database

    def add(self, operation: Operation, native_request: Content, batch: Sequence[Content] = ()) -> None:
        """The operation and the paused native request it answers are stored together, or neither is. ``batch`` is
        every tool call of the paused model turn, re-sent with the answer (see ``approval_gate.batch_of``)."""
        with self._database.atomically():
            self._database.execute(
                "INSERT INTO operations(operation_id, approval_id, conversation_id, owner, status, json) VALUES (?, ?, ?, ?, ?, ?)",
                (
                    operation.operation_id,
                    operation.approval_id,
                    operation.conversation_id,
                    operation.owner,
                    str(operation.status),
                    operation.to_json(),
                ),
            )
            self._database.execute(
                "INSERT INTO native_approval_requests(operation_id, json) VALUES (?, ?)",
                (
                    operation.operation_id,
                    json.dumps({"request": native_request.to_dict(), "batch": [call.to_dict() for call in batch]}),
                ),
            )

    def _one(self, column: str, value: str) -> Operation | None:
        rows = self._database.query(f"SELECT json FROM operations WHERE {column} = ?", (value,))  # noqa: S608 - fixed column names
        return Operation.from_json(rows[0][0]) if rows else None

    def find(self, operation_id: str) -> Operation | None:
        return self._one("operation_id", operation_id)

    def find_by_approval(self, approval_id: str) -> Operation | None:
        return self._one("approval_id", approval_id)

    def for_conversation(self, conversation_id: str) -> list[Operation]:
        rows = self._database.query("SELECT json FROM operations WHERE conversation_id = ?", (conversation_id,))
        return sorted((Operation.from_json(text) for (text,) in rows), key=lambda op: op.created_at)

    def try_update(self, operation: Operation, expected: Status) -> bool:
        """Stores the operation only if its stored status is still ``expected``; otherwise takes the stored record."""
        changed = self._database.execute(
            "UPDATE operations SET status = ?, json = ? WHERE operation_id = ? AND status = ?",
            (str(operation.status), operation.to_json(), operation.operation_id, str(expected)),
        )
        if changed == 1:
            return True
        if (stored := self.find(operation.operation_id)) is not None:
            operation.refresh_from(stored)
        return False

    def expire_if_due(self, operation: Operation, at: datetime) -> bool:
        if operation.status is Status.AWAITING_APPROVAL and operation.expire_if_due(at):
            self.try_update(operation, Status.AWAITING_APPROVAL)
        return operation.status is Status.EXPIRED

    def expire(self, operation: Operation, at: datetime) -> None:
        """Withdraws an approval that can no longer be shown or answered (its turn failed or was never stored)."""
        if operation.status is Status.AWAITING_APPROVAL:
            operation.status, operation.updated_at = Status.EXPIRED, at
            self.try_update(operation, Status.AWAITING_APPROVAL)

    def decide(self, operation: Operation, decision: Decision, reason: str | None, at: datetime) -> bool:
        """Records the customer's decision once: the first stored decision wins."""
        if not operation.decide(decision, at):
            return False
        operation.decision_reason = reason
        return self.try_update(operation, Status.AWAITING_APPROVAL)

    def fail(self, operation: Operation, detail: str, at: datetime, code: str = "internal_error") -> None:
        """An approved write that could not be carried out. Only a still-running operation can fail."""
        if operation.status is Status.RUNNING:
            operation.status, operation.failure, operation.updated_at = Status.FAILED, (code, detail), at
            self.try_update(operation, Status.RUNNING)

    def native_request(self, operation_id: str) -> tuple[Content, list[Content]] | None:
        """The paused native request and the tool-call batch it belongs to."""
        rows = self._database.query("SELECT json FROM native_approval_requests WHERE operation_id = ?", (operation_id,))
        if not rows:
            return None
        stored: dict[str, Any] = json.loads(rows[0][0])
        if "request" not in stored:  # stored before L20: the request alone
            return Content.from_dict(stored), []
        return Content.from_dict(stored["request"]), [Content.from_dict(call) for call in stored["batch"]]

    def answered(self, operation_id: str) -> None:
        """The paused request was answered: a later retry reports from the record and never resumes it again."""
        self._database.execute("DELETE FROM native_approval_requests WHERE operation_id = ?", (operation_id,))


_approved: ContextVar[Operation | None] = ContextVar("approved_operation", default=None)


@contextmanager
def approval_scope(operation: Operation) -> Generator[Operation]:
    """The approved operation a resumed run may execute, set by trusted server code (never by the model)."""
    token = _approved.set(operation)
    try:
        yield operation
    finally:
        _approved.reset(token)


def _text(arguments: Mapping[str, Any], name: str) -> str | None:
    value = arguments.get(name)
    return None if value is None else str(value).strip()


def _printable(*values: str | None) -> bool:
    return all(value is None or all(ch.isprintable() or ch in "\n\t" for ch in value) for value in values)


def map_arguments(kind: Kind, arguments: Mapping[str, Any], priorities: tuple[str, ...]) -> dict[str, str]:
    """Validates a model tool call into contract arguments (snake_case). Used when proposing and when executing."""
    order_id = (_text(arguments, "order_id") or "").upper()
    if kind is Kind.CANCEL_ORDER:
        if not ORDER_ID.match(order_id):
            raise WriteError("invalid_arguments", "order_id must look like ORD-1234.")
        return {"order_id": order_id}
    if kind is Kind.REFUND_ORDER:
        reason = _text(arguments, "reason")
        if not ORDER_ID.match(order_id):
            raise WriteError("invalid_arguments", "order_id must look like ORD-1234.")
        if not reason or len(reason) > 500 or not _printable(reason):
            raise WriteError("invalid_arguments", "reason must be 1 to 500 printable characters.")
        return {"order_id": order_id, "reason": reason}
    subject, description = _text(arguments, "subject"), _text(arguments, "description")
    priority = (_text(arguments, "priority") or "normal").lower()
    related = (_text(arguments, "related_order_id") or "").upper()
    if (
        not subject
        or len(subject) > 120
        or not description
        or len(description) > 2000
        or not _printable(subject, description)
    ):
        raise WriteError("invalid_arguments", "subject must be 1 to 120 characters and description 1 to 2000.")
    if priority not in priorities:
        raise WriteError("invalid_arguments", "priority must be low, normal, or high.")
    mapped = {"subject": subject, "description": description, "priority": priority}
    if related:
        if not ORDER_ID.match(related):
            raise WriteError("invalid_arguments", "related_order_id must look like ORD-1234.")
        mapped["related_order_id"] = related
    return mapped


@dataclass
class WriteHandlers:
    """Each write, by the module that owns its data, over the simulated state. Owner and eligibility checked each time."""

    state: SimulatedState
    policy: Policy

    def _owned(self, principal: Principal, order_id: str | None) -> Order:
        order = self.state.order(order_id or "")
        if order is None or order.customer_id != principal.customer_id:
            raise not_found()
        return order

    def check(self, principal: Principal, kind: Kind, arguments: Mapping[str, str]) -> str:
        """Checks eligibility without changing anything; returns a human-readable summary."""
        if kind is Kind.CANCEL_ORDER:
            order = self._owned(principal, arguments["order_id"])
            if reason := self.policy.why_not_cancellable(order):
                raise WriteError("not_eligible", reason)
            return f"Cancel order {order.order_id} ({', '.join(item.name for item in order.items)})."
        if kind is Kind.REFUND_ORDER:
            order = self._owned(principal, arguments["order_id"])
            invoice = self._invoice(order)
            if reason := self.policy.why_not_refundable(order):
                raise WriteError("not_eligible", reason)
            return (
                f"Refund order {order.order_id} in full ({invoice.total.display()}) for reason: {arguments['reason']}"
            )
        if related := arguments.get("related_order_id"):
            self._owned(principal, related)
        return f"Open a {arguments['priority']}-priority support ticket: {arguments['subject']}"

    def execute(self, principal: Principal, kind: Kind, arguments: Mapping[str, str], at: datetime) -> dict[str, Any]:
        """Re-checks authoritatively and performs the write. Returns the contract result object."""
        self.check(principal, kind, arguments)
        if kind is Kind.CANCEL_ORDER:
            order = self._owned(principal, arguments["order_id"])
            self.state.save_order(order.model_copy(update={"status": "cancelled"}))
            return {"order_id": order.order_id, "order_status": "cancelled"}
        if kind is Kind.REFUND_ORDER:
            order = self._owned(principal, arguments["order_id"])
            invoice = self._invoice(order)
            refund_id = self.state.next_refund_id()
            payment = Payment(
                payment_id=self.state.next_payment_id(),
                customer_id=invoice.customer_id,
                order_id=invoice.order_id,
                invoice_id=invoice.invoice_id,
                paid_at=at,
                amount=invoice.total,
                method=invoice.payment_method,
                kind="refund",
            )
            self.state.add_payment(payment)
            self.state.save_order(order.model_copy(update={"refund_status": "refunded"}))
            return {
                "refund_id": refund_id,
                "order_id": order.order_id,
                "amount": {"amount_minor": invoice.total.amount_minor, "currency": invoice.total.currency},
            }
        ticket = Ticket(
            self.state.next_ticket_id(),
            principal.customer_id,
            arguments["subject"],
            arguments["description"],
            arguments["priority"],
            arguments.get("related_order_id"),
            at,
        )
        self.state.add_ticket(ticket)
        return {"ticket_id": ticket.ticket_id}

    def _invoice(self, order: Order) -> Any:
        invoice = next((invoice for invoice in self.state.invoices if invoice.order_id == order.order_id), None)
        if invoice is None:
            raise not_found()
        return invoice


@dataclass
class OperationService:
    store: OperationStore
    handlers: WriteHandlers
    policy: Policy
    database: Database
    # Test hook (like the fake model's before_call): awaited just before the pre-commit boundary. Never set in the app.
    before_commit: Callable[[], Awaitable[None]] | None = None

    def propose(
        self,
        principal: Principal,
        conversation_id: str,
        specialist: str,
        source_turn_id: str,
        tool: str,
        arguments: Mapping[str, Any],
        native_request: Content,
        batch: Sequence[Content] = (),
    ) -> Operation:
        """Validates the tool call and checks eligibility before asking; only then creates a bound operation."""
        try:
            kind = Kind(tool)
        except ValueError:
            raise WriteError("invalid_arguments", "Unknown write tool.") from None
        try:
            mapped = map_arguments(kind, arguments, self.policy.ticket_priorities)
            summary = self.handlers.check(principal, kind, mapped)
        except WriteError as refused:
            operation_metrics.add(1, {"support.operation.kind": str(kind), "support.operation.outcome": refused.code})
            raise
        at = now()
        operation = Operation(
            "op_" + secrets.token_hex(8),
            "appr_" + secrets.token_hex(8),
            conversation_id,
            principal.customer_id,
            kind,
            mapped,
            summary if len(summary) <= MAX_SUMMARY else summary[: MAX_SUMMARY - 1] + "…",
            specialist,
            source_turn_id,
            at,
            at + timedelta(minutes=self.policy.approval_expiry_minutes),
            at,
        )
        self.store.add(operation, native_request, batch)
        operation_metrics.add(
            1, {"support.operation.kind": str(kind), "support.operation.outcome": "awaiting_approval"}
        )
        return operation

    async def execute_approved(self, principal: Principal, kind: Kind, arguments: Mapping[str, Any]) -> dict[str, Any]:
        """Runs a write only for the approved operation in scope: same customer, same kind, exactly the same arguments, once.

        The pre-commit boundary (L20): once the turn's cancellation was requested, no write starts. A write that already
        committed stays committed; cancellation never rolls it back."""
        operation = _approved.get()
        mapped = map_arguments(kind, arguments, self.policy.ticket_priorities)
        if (
            operation is None
            or operation.kind is not kind
            or canonical(operation.arguments) != canonical(mapped)
            or operation.owner != principal.customer_id
        ):
            raise WriteError("not_approved", "This exact action has not been approved.")
        if operation.result is not None:
            return operation.result  # this run already committed it
        if self.before_commit is not None:
            await self.before_commit()
        raise_if_cancelled()
        at = now()
        failure: WriteError | None = None
        try:
            # One transaction: the business change and the "committed" record are stored together or not at all.
            with self.database.atomically():
                stored = self.store.find(operation.operation_id)
                if stored is None or stored.status is not Status.RUNNING:
                    # Another run already finished it (or it was never approved): report the stored record, write nothing.
                    if stored is not None:
                        operation.refresh_from(stored)
                    if operation.status is Status.COMMITTED and operation.result is not None:
                        return operation.result
                    raise WriteError("not_approved", "This action is not awaiting execution.")
                try:
                    result = self.handlers.execute(principal, kind, stored.arguments, at)
                except WriteError as error:
                    failure = error
                    code = error.code if error.code in ("not_eligible", "not_found") else "internal_error"
                    stored.status, stored.failure, stored.updated_at = Status.FAILED, (code, error.detail), at
                    self.store.try_update(stored, Status.RUNNING)
                else:
                    stored.status, stored.result, stored.updated_at = Status.COMMITTED, result, at
                    if not self.store.try_update(stored, Status.RUNNING):
                        operation.refresh_from(stored)  # try_update loaded the stored record into `stored`
                        raise Rollback  # a stale run: undo this write and report what is stored
                operation.refresh_from(stored)
        except Rollback:
            if operation.status is Status.COMMITTED and operation.result is not None:
                return operation.result
            raise WriteError("not_approved", "This action is not awaiting execution.") from None
        outcome = "failed" if failure is not None else "committed"
        operation_metrics.add(1, {"support.operation.kind": str(kind), "support.operation.outcome": outcome})
        if failure is not None:
            raise failure
        assert operation.result is not None
        return operation.result


def build_operations(state: SimulatedState, database: Database, policy: Policy | None = None) -> OperationService:
    policy = policy or Policy.load()
    return OperationService(OperationStore(database), WriteHandlers(state, policy), policy, database)
