"""The simulated business state (L37): orders, payments, and tickets that approved writes may change.

It starts as a copy of the shared fixtures; the fixture files are never changed. In memory only: durability and
atomic duplicate protection arrive in L38.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime

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


@dataclass
class SimulatedState:
    orders: dict[str, Order]
    invoices: tuple[Invoice, ...]
    payments: list[Payment]
    tickets: list[Ticket] = field(default_factory=list[Ticket])
    next_refund: int = 1
    next_payment: int = 8001
    next_ticket: int = 1

    @classmethod
    def from_fixtures(cls, fixtures: Fixtures) -> SimulatedState:
        return cls(orders=dict(fixtures.orders), invoices=fixtures.invoices, payments=list(fixtures.payments))

    def tickets_of(self, customer_id: str) -> list[Ticket]:
        return [ticket for ticket in self.tickets if ticket.customer_id == customer_id]
