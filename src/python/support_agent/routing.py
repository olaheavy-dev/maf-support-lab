"""Triage output and the deterministic routing rules (L18, porting L07).

The model only classifies. Its output is untrusted until it passes the full triage-decision contract schema; then
code decides: the requested action must belong to the target's team, "other" is never routed, and actions about one
specific order need an order id. Invalid or ambiguous output is never guessed at: it becomes an unrouted clarification.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from enum import StrEnum
from typing import Any, Literal, cast

from pydantic import BaseModel, ConfigDict

from support_agent import contract


class TriageDecision(BaseModel):
    """The shape the triage model is asked for (structured output). The full contract schema is checked separately."""

    model_config = ConfigDict(extra="forbid")

    target: Literal["orders", "billing", "technical"]
    requested_action: Literal[
        "order_status",
        "cancel_order",
        "refund_order",
        "invoice",
        "payment_history",
        "troubleshooting",
        "create_ticket",
        "other",
    ]
    order_id: str | None
    summary: str
    customer_sentiment: Literal["frustrated", "neutral", "positive"]
    confidence: float


class UnroutedReason(StrEnum):
    INVALID_MODEL_OUTPUT = "invalid_model_output"
    AMBIGUOUS_REQUEST = "ambiguous_request"


TEAM: dict[str, str] = {
    "order_status": "orders",
    "cancel_order": "orders",
    "refund_order": "billing",
    "invoice": "billing",
    "payment_history": "billing",
    "troubleshooting": "technical",
    "create_ticket": "technical",
}
NEEDS_ORDER_ID = {"order_status", "cancel_order", "refund_order"}
WRITE_ACTIONS = {"cancel_order", "refund_order", "create_ticket"}


@dataclass(frozen=True, slots=True)
class TurnRoute:
    target: str | None = None
    unrouted_reason: UnroutedReason | None = None

    def to_contract(self) -> dict[str, Any]:
        if self.target is not None:
            return {"status": "routed", "target": self.target}
        # The contract always has "target": null when unrouted (as the .NET backend sends it).
        return {
            "status": "unrouted",
            "target": None,
            "unrouted_reason": str(self.unrouted_reason or UnroutedReason.AMBIGUOUS_REQUEST),
        }


@dataclass(frozen=True, slots=True)
class TriageOutcome:
    """What the specialists receive: the customer's original message kept beside the validated triage fields."""

    original_message: str
    decision: dict[str, Any] | None
    route: TurnRoute
    conversation_id: str | None = None
    extra: dict[str, Any] = field(default_factory=dict[str, Any])

    @property
    def action(self) -> str | None:
        return None if self.decision is None else self.decision["requested_action"]

    @property
    def order_id(self) -> str | None:
        return None if self.decision is None else self.decision.get("order_id")

    @property
    def is_missing_order_id(self) -> bool:
        return self.route.target == "orders" and self.order_id is None and self.action in NEEDS_ORDER_ID

    @property
    def is_write(self) -> bool:
        return self.route.target is not None and self.action in WRITE_ACTIONS


def parse(text: str) -> dict[str, Any] | None:
    """The decision if the text is JSON that passes the full contract schema; otherwise None (never guessed)."""
    try:
        document: Any = json.loads(text)
    except ValueError:  # JSONDecodeError is a ValueError
        return None
    if not isinstance(document, dict) or not contract.is_valid("triage-decision", document):
        return None
    return cast(dict[str, Any], document)


def route(decision: dict[str, Any] | None) -> TurnRoute:
    if decision is None:
        return TurnRoute(unrouted_reason=UnroutedReason.INVALID_MODEL_OUTPUT)
    team = TEAM.get(decision["requested_action"])
    return (
        TurnRoute(target=team)
        if team == decision["target"]
        else TurnRoute(unrouted_reason=UnroutedReason.AMBIGUOUS_REQUEST)
    )
