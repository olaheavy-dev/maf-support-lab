"""The simulated business state (durable): orders, payments, and tickets that approved writes change.

Orders start from the shared fixtures; an order a write changed is stored in the database and wins over the fixture
(the fixture files are never changed). Refund payments and tickets are stored with their id counters. Inside
``Database.atomically`` every change joins that transaction.
"""

from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from datetime import datetime
from typing import Any

from support_agent.database import Database
from support_agent.domain import Invoice, Order, Payment
from support_agent.fixtures import Fixtures


@dataclass(frozen=True, slots=True)
class Ticket:
    ticket_id: str
    customer_id: str
    subject: str
    description: str
    priority: str
    related_order_id: str | None
    created_at: datetime

    def to_json(self) -> str:
        data: dict[str, Any] = asdict(self)
        data["created_at"] = self.created_at.isoformat()
        return json.dumps(data)

    @classmethod
    def from_json(cls, text: str) -> Ticket:
        data: dict[str, Any] = json.loads(text)
        data["created_at"] = datetime.fromisoformat(data["created_at"])
        return cls(**data)


class SimulatedState:
    def __init__(self, fixtures: Fixtures, database: Database) -> None:
        self._fixtures = fixtures
        self._database = database
        self.invoices: tuple[Invoice, ...] = fixtures.invoices

    def order(self, order_id: str) -> Order | None:
        changed = self._database.query("SELECT json FROM changed_orders WHERE order_id = ?", (order_id,))
        return Order.model_validate_json(changed[0][0]) if changed else self._fixtures.orders.get(order_id)

    @property
    def orders(self) -> dict[str, Order]:
        """A read-only snapshot: the fixtures overlaid with the stored changes."""
        merged = dict(self._fixtures.orders)
        for (text,) in self._database.query("SELECT json FROM changed_orders"):
            order = Order.model_validate_json(text)
            merged[order.order_id] = order
        return merged

    def save_order(self, order: Order) -> None:
        self._database.execute(
            "INSERT INTO changed_orders(order_id, json) VALUES (?, ?) ON CONFLICT(order_id) DO UPDATE SET json = excluded.json",
            (order.order_id, order.model_dump_json()),
        )

    @property
    def payments(self) -> list[Payment]:
        stored = [
            Payment.model_validate_json(text)
            for (text,) in self._database.query("SELECT json FROM payments ORDER BY payment_id")
        ]
        return [*self._fixtures.payments, *stored]

    def add_payment(self, payment: Payment) -> None:
        self._database.execute(
            "INSERT INTO payments(payment_id, customer_id, json) VALUES (?, ?, ?)",
            (payment.payment_id, payment.customer_id, payment.model_dump_json()),
        )

    def tickets_of(self, customer_id: str) -> list[Ticket]:
        rows = self._database.query("SELECT json FROM tickets WHERE customer_id = ? ORDER BY ticket_id", (customer_id,))
        return [Ticket.from_json(text) for (text,) in rows]

    def add_ticket(self, ticket: Ticket) -> None:
        self._database.execute(
            "INSERT INTO tickets(ticket_id, customer_id, json) VALUES (?, ?, ?)",
            (ticket.ticket_id, ticket.customer_id, ticket.to_json()),
        )

    def next_refund_id(self) -> str:
        return f"RFD-{self._database.next_value('refund', 1):04d}"

    def next_payment_id(self) -> str:
        return f"PAY-{self._database.next_value('payment', 8001)}"

    def next_ticket_id(self) -> str:
        return f"TKT-{self._database.next_value('ticket', 1):04d}"
