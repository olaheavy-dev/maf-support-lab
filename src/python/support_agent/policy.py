"""Business policy from fixtures/data/policies.json (shared with .NET): eligibility rules and the approval window."""

from __future__ import annotations

import json
from dataclasses import dataclass
from datetime import datetime, timedelta
from pathlib import Path
from typing import Any

from support_agent.domain import Order
from support_agent.fixtures import FIXTURES


@dataclass(frozen=True, slots=True)
class Policy:
    reference_time: datetime
    cancellable_statuses: frozenset[str]
    refund_required_status: str
    refund_window_days: int
    refundable_refund_statuses: frozenset[str]
    ticket_priorities: tuple[str, ...]
    approval_expiry_minutes: int

    @classmethod
    def load(cls, folder: Path = FIXTURES) -> Policy:
        data: dict[str, Any] = json.loads((folder / "policies.json").read_text(encoding="utf-8"))
        return cls(
            reference_time=datetime.fromisoformat(data["reference_time"]),
            cancellable_statuses=frozenset(data["cancellation"]["allowed_statuses"]),
            refund_required_status=data["refund"]["required_status"],
            refund_window_days=int(data["refund"]["window_days"]),
            refundable_refund_statuses=frozenset(data["refund"]["eligible_refund_statuses"]),
            ticket_priorities=tuple(data["support_ticket"]["priorities"]),
            approval_expiry_minutes=int(data["approval"]["expires_after_minutes"]),
        )

    def why_not_cancellable(self, order: Order) -> str | None:
        if order.status in self.cancellable_statuses:
            return None
        return f"Order {order.order_id} is {order.status}; only orders that are still processing can be cancelled."

    def why_not_refundable(self, order: Order) -> str | None:
        """Delivered, not refunded before, and within the window counted from delivery to the reference time."""
        if order.status != self.refund_required_status or order.delivered_at is None:
            return f"Order {order.order_id} is {order.status}; only delivered orders can be refunded."
        if order.refund_status not in self.refundable_refund_statuses:
            return f"Order {order.order_id} has already been refunded."
        if self.reference_time - order.delivered_at > timedelta(days=self.refund_window_days):
            return f"Order {order.order_id} was delivered more than {self.refund_window_days} days ago, outside the refund window."
        return None
