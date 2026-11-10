"""Records from the shared synthetic fixtures (fixtures/data). Shapes match the .NET backend's; read-only here."""

from __future__ import annotations

from datetime import datetime

from pydantic import BaseModel, ConfigDict


class Record(BaseModel):
    model_config = ConfigDict(frozen=True, extra="ignore")


class Money(Record):
    amount_minor: int
    currency: str

    def display(self) -> str:
        return (
            f"${self.amount_minor / 100:,.2f}"
            if self.currency == "USD"
            else f"{self.amount_minor / 100:,.2f} {self.currency}"
        )


class OrderItem(Record):
    sku: str
    name: str
    quantity: int
    unit_price: Money


class Order(Record):
    order_id: str
    customer_id: str
    status: str
    placed_at: datetime
    shipped_at: datetime | None = None
    delivered_at: datetime | None = None
    refund_status: str
    items: tuple[OrderItem, ...]
    total: Money


class InvoiceLine(Record):
    description: str
    quantity: int
    amount: Money


class Invoice(Record):
    invoice_id: str
    order_id: str
    customer_id: str
    issued_at: datetime
    lines: tuple[InvoiceLine, ...]
    subtotal: Money
    tax: Money
    total: Money
    payment_method: str


class Payment(Record):
    payment_id: str
    customer_id: str
    order_id: str
    invoice_id: str
    paid_at: datetime
    amount: Money
    method: str
    kind: str


class KnowledgeArticle(Record):
    article_id: str
    title: str
    keywords: tuple[str, ...]
    steps: tuple[str, ...]

    def score(self, terms: set[str]) -> int:
        """How many keywords the terms mention: exact, or a plural/inflection of at least four letters (as in .NET)."""
        return sum(
            1
            for keyword in self.keywords
            if any(
                term == keyword.lower()
                or (
                    len(term) >= 4
                    and len(keyword) >= 4
                    and (term.startswith(keyword.lower()) or keyword.lower().startswith(term))
                )
                for term in terms
            )
        )
