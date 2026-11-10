"""The agents' read-only tools (L18). Each takes ids and nothing else: never a customer id, never a confirmation.

The customer is the pinned principal (identity.current_principal), so a model cannot choose whose records it reads.
Each tool records its outcome in the turn activity, which decides the reply kind. There is deliberately no tool that
changes anything: writes arrive with the approval gate (L37).
"""

from __future__ import annotations

from typing import Annotated, Any

from agent_framework import FunctionTool, tool
from pydantic import Field

from support_agent.activity import ToolOutcome, current_activity
from support_agent.identity import current_principal
from support_agent.services import InvalidReferenceError, Services, normalize

ORDER_STATUS = "get_order_status"
INVOICE = "get_invoice"
PAYMENT_HISTORY = "get_payment_history"
KNOWLEDGE_SEARCH = "search_knowledge_base"


def _record(outcome: ToolOutcome, verified_order_id: str | None = None) -> None:
    activity = current_activity()
    if activity is not None:
        activity.record(outcome, verified_order_id)


def order_tools(services: Services) -> list[FunctionTool]:
    @tool(
        name=ORDER_STATUS,
        description="Look up the status of one of the current customer's orders. Use it whenever the customer asks about a specific order.",
    )
    def get_order_status(
        order_id: Annotated[str, Field(description="The order id, in the form ORD-1234.")],
    ) -> dict[str, Any]:
        try:
            order = services.orders.order_status(current_principal(), order_id)
        except InvalidReferenceError:
            _record(ToolOutcome.INVALID_INPUT)
            return {"lookup": "invalid_order_id"}
        if order is None:
            _record(ToolOutcome.NOT_FOUND)
            return {"lookup": "not_found", "order_id": normalize(order_id)}
        _record(ToolOutcome.FOUND, order.order_id)
        return {
            "lookup": "found",
            "order_id": order.order_id,
            "order_status": order.status,
            "placed_at": order.placed_at.isoformat(),
            "shipped_at": order.shipped_at.isoformat() if order.shipped_at else None,
            "delivered_at": order.delivered_at.isoformat() if order.delivered_at else None,
            "items": [item.name for item in order.items],
        }

    return [get_order_status]


def billing_tools(services: Services) -> list[FunctionTool]:
    @tool(
        name=INVOICE,
        description="Look up one of the current customer's invoices, by invoice id (INV-1234) or by order id (ORD-1234).",
    )
    def get_invoice(
        reference: Annotated[str, Field(description="The invoice id (INV-1234) or order id (ORD-1234).")],
    ) -> dict[str, Any]:
        try:
            invoice = services.billing.invoice(current_principal(), reference)
        except InvalidReferenceError:
            _record(ToolOutcome.INVALID_INPUT)
            return {"lookup": "invalid_reference"}
        if invoice is None:
            _record(ToolOutcome.NOT_FOUND)
            return {"lookup": "not_found", "reference": normalize(reference)}
        _record(ToolOutcome.FOUND, invoice.order_id)
        return {
            "lookup": "found",
            "invoice_id": invoice.invoice_id,
            "order_id": invoice.order_id,
            "issued_at": invoice.issued_at.isoformat(),
            "total": invoice.total.display(),
            "payment_method": invoice.payment_method,
            "lines": [line.description for line in invoice.lines],
        }

    @tool(name=PAYMENT_HISTORY, description="List the current customer's payments and refunds, newest first.")
    def get_payment_history() -> dict[str, Any]:
        payments = services.billing.payment_history(current_principal())
        _record(ToolOutcome.FOUND)
        return {
            "payments": [
                {
                    "payment_id": payment.payment_id,
                    "kind": payment.kind,
                    "order_id": payment.order_id,
                    "amount": payment.amount.display(),
                    "paid_at": payment.paid_at.isoformat(),
                }
                for payment in payments
            ]
        }

    return [get_invoice, get_payment_history]


def technical_tools(services: Services) -> list[FunctionTool]:
    @tool(
        name=KNOWLEDGE_SEARCH,
        description="Search VoltCart's troubleshooting articles for a product problem. Use the product and the symptom as the query, for example 'headphones crackling'.",
    )
    def search_knowledge_base(
        query: Annotated[str, Field(description="Product and symptom keywords, for example 'speaker wifi setup'.")],
    ) -> dict[str, Any]:
        try:
            articles = services.knowledge.search(query)
        except InvalidReferenceError:
            return {"search": "invalid_query", "articles": []}
        _record(ToolOutcome.FOUND if articles else ToolOutcome.NO_MATCH)
        return {
            "search": "found" if articles else "no_match",
            "articles": [{"article_id": a.article_id, "title": a.title, "steps": list(a.steps)} for a in articles],
        }

    return [search_knowledge_base]
