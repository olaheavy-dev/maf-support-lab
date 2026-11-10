"""Writes and their approvals (L37, porting L10).

A write is proposed as an ``Operation`` bound, when created, to the customer, the conversation, the kind, and the
exact arguments (canonical JSON). The customer's decision is recorded once; an approved write runs at most once, and
only inside an ``approval_scope`` naming exactly that operation. Eligibility and ownership are checked by the module
that owns the data, both before asking (so customers aren't asked to approve impossible actions) and again,
authoritatively, at execution. Approval is human consent; these checks are business authorization. Both are needed.
In memory only: durability and atomic duplicate protection arrive in L38.
"""

from __future__ import annotations

import json
import re
import secrets
from collections.abc import Generator, Mapping
from contextlib import contextmanager
from contextvars import ContextVar
from dataclasses import dataclass, field
from datetime import UTC, datetime, timedelta
from enum import StrEnum
from typing import Any

from support_agent.domain import Order, Payment
from support_agent.identity import Principal
from support_agent.policy import Policy
from support_agent.state import SimulatedState, Ticket

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
    executing: bool = False

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
    def __init__(self) -> None:
        self._by_id: dict[str, Operation] = {}

    def add(self, operation: Operation) -> None:
        self._by_id[operation.operation_id] = operation

    def find(self, operation_id: str) -> Operation | None:
        return self._by_id.get(operation_id)

    def find_by_approval(self, approval_id: str) -> Operation | None:
        return next((op for op in self._by_id.values() if op.approval_id == approval_id), None)

    def for_conversation(self, conversation_id: str) -> list[Operation]:
        return sorted(
            (op for op in self._by_id.values() if op.conversation_id == conversation_id), key=lambda op: op.created_at
        )


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
        order = self.state.orders.get(order_id or "")
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
            self.state.orders[order.order_id] = order.model_copy(update={"status": "cancelled"})
            return {"order_id": order.order_id, "order_status": "cancelled"}
        if kind is Kind.REFUND_ORDER:
            order = self._owned(principal, arguments["order_id"])
            invoice = self._invoice(order)
            refund_id = f"RFD-{self.state.next_refund:04d}"
            payment = Payment(
                payment_id=f"PAY-{self.state.next_payment}",
                customer_id=invoice.customer_id,
                order_id=invoice.order_id,
                invoice_id=invoice.invoice_id,
                paid_at=at,
                amount=invoice.total,
                method=invoice.payment_method,
                kind="refund",
            )
            self.state.next_refund += 1
            self.state.next_payment += 1
            self.state.payments.append(payment)
            self.state.orders[order.order_id] = order.model_copy(update={"refund_status": "refunded"})
            return {
                "refund_id": refund_id,
                "order_id": order.order_id,
                "amount": {"amount_minor": invoice.total.amount_minor, "currency": invoice.total.currency},
            }
        ticket = Ticket(
            f"TKT-{self.state.next_ticket:04d}",
            principal.customer_id,
            arguments["subject"],
            arguments["description"],
            arguments["priority"],
            arguments.get("related_order_id"),
            at,
        )
        self.state.next_ticket += 1
        self.state.tickets.append(ticket)
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
    pending_requests: dict[str, Any] = field(default_factory=dict[str, Any])  # operation id → native approval request

    def propose(
        self,
        principal: Principal,
        conversation_id: str,
        specialist: str,
        source_turn_id: str,
        tool: str,
        arguments: Mapping[str, Any],
    ) -> Operation:
        """Validates the tool call and checks eligibility before asking; only then creates a bound operation."""
        try:
            kind = Kind(tool)
        except ValueError:
            raise WriteError("invalid_arguments", "Unknown write tool.") from None
        mapped = map_arguments(kind, arguments, self.policy.ticket_priorities)
        summary = self.handlers.check(principal, kind, mapped)
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
        self.store.add(operation)
        return operation

    def execute_approved(self, principal: Principal, kind: Kind, arguments: Mapping[str, Any]) -> dict[str, Any]:
        """Runs a write only for the approved operation in scope: same customer, same kind, exactly the same arguments, once."""
        operation = _approved.get()
        mapped = map_arguments(kind, arguments, self.policy.ticket_priorities)
        if (
            operation is None
            or operation.kind is not kind
            or canonical(operation.arguments) != canonical(mapped)
            or operation.owner != principal.customer_id
        ):
            raise WriteError("not_approved", "This exact action has not been approved.")
        if (
            operation.status is not Status.RUNNING
            or operation.executing
            or operation.result is not None
            or operation.failure is not None
        ):
            if operation.result is not None:
                return operation.result
            raise WriteError("not_approved", "This action is not awaiting execution.")
        operation.executing = True
        at = now()
        try:
            result = self.handlers.execute(principal, kind, operation.arguments, at)
        except WriteError as error:
            operation.status, operation.failure, operation.updated_at = (
                Status.FAILED,
                (error.code if error.code in ("not_eligible", "not_found") else "internal_error", error.detail),
                at,
            )
            raise
        operation.status, operation.result, operation.updated_at = Status.COMMITTED, result, at
        return result


def build_operations(state: SimulatedState, policy: Policy | None = None) -> OperationService:
    policy = policy or Policy.load()
    return OperationService(OperationStore(), WriteHandlers(state, policy), policy)
