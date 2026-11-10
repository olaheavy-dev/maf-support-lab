"""Read-only record access, scoped to the trusted principal (L18).

Ownership is decided here, in application code, never by the model: another customer's record is reported exactly like
a missing one (None). Ids are validated before any lookup. Nothing in this module changes state.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

from support_agent.database import Database
from support_agent.domain import Invoice, KnowledgeArticle, Order, Payment
from support_agent.fixtures import Fixtures
from support_agent.identity import Principal
from support_agent.state import SimulatedState

ORDER_ID = re.compile(r"^ORD-[0-9]{4}$")
INVOICE_ID = re.compile(r"^INV-[0-9]{4}$")
SEARCH_TERMS = re.compile(r"[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*")
MAX_QUERY_LENGTH = 200
MAX_ARTICLES = 3


def normalize(reference: str | None) -> str:
    return (reference or "").strip().upper()


class InvalidReferenceError(ValueError):
    """The id is not in a valid form (for example ORD-1234 or INV-1234)."""


@dataclass(frozen=True, slots=True)
class OrderService:
    state: SimulatedState

    def order_status(self, principal: Principal, order_id: str) -> Order | None:
        normalized = normalize(order_id)
        if not ORDER_ID.match(normalized):
            raise InvalidReferenceError(order_id)
        order = self.state.order(normalized)
        return order if order is not None and order.customer_id == principal.customer_id else None


@dataclass(frozen=True, slots=True)
class BillingService:
    state: SimulatedState

    def invoice(self, principal: Principal, reference: str) -> Invoice | None:
        """An invoice by its own id (INV-1234) or by its order (ORD-1234); only the caller's own."""
        normalized = normalize(reference)
        if INVOICE_ID.match(normalized):
            found = next((invoice for invoice in self.state.invoices if invoice.invoice_id == normalized), None)
        elif ORDER_ID.match(normalized):
            found = next((invoice for invoice in self.state.invoices if invoice.order_id == normalized), None)
        else:
            raise InvalidReferenceError(reference)
        return found if found is not None and found.customer_id == principal.customer_id else None

    def payment_history(self, principal: Principal) -> list[Payment]:
        payments = [payment for payment in self.state.payments if payment.customer_id == principal.customer_id]
        return sorted(payments, key=lambda payment: payment.paid_at, reverse=True)


@dataclass(frozen=True, slots=True)
class KnowledgeService:
    fixtures: Fixtures

    def search(self, query: str) -> list[KnowledgeArticle]:
        """Keyword search over article keywords only (not titles), best first, at most three. Public help, not customer data."""
        text = query or ""
        if len(text) > MAX_QUERY_LENGTH or any(ch.isprintable() is False and ch not in "\n\t" for ch in text):
            raise InvalidReferenceError("query")
        terms = {match.group(0).lower() for match in SEARCH_TERMS.finditer(text)}
        scored = [(article.score(terms), article) for article in self.fixtures.articles]
        matches = sorted((pair for pair in scored if pair[0] > 0), key=lambda pair: (-pair[0], pair[1].article_id))
        return [article for _, article in matches[:MAX_ARTICLES]]


@dataclass(frozen=True, slots=True)
class Services:
    orders: OrderService
    billing: BillingService
    knowledge: KnowledgeService
    state: SimulatedState

    @classmethod
    def create(cls, fixtures: Fixtures, database: Database) -> Services:
        """The services over the simulated state stored in this host's database (L38)."""
        state = SimulatedState(fixtures, database)
        return cls(OrderService(state), BillingService(state), KnowledgeService(fixtures), state)
