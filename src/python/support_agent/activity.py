"""What the tools found during one specialist run (L18), recorded in code, never inferred from the model's wording.

The reply kind comes from these facts: anything found is an answer; otherwise a missing record is not_found; a search
that matched nothing is an escalation offer; no lookup at all means the specialist asked for more information.
"""

from __future__ import annotations

from collections.abc import Generator
from contextlib import contextmanager
from contextvars import ContextVar
from dataclasses import dataclass, field
from enum import StrEnum


class ToolOutcome(StrEnum):
    FOUND = "found"
    NOT_FOUND = "not_found"
    INVALID_INPUT = "invalid_input"
    NO_MATCH = "no_match"


@dataclass
class TurnActivity:
    outcomes: list[ToolOutcome] = field(default_factory=list[ToolOutcome])
    verified_order_id: str | None = None

    def record(self, outcome: ToolOutcome, verified_order_id: str | None = None) -> None:
        self.outcomes.append(outcome)
        if verified_order_id is not None:
            self.verified_order_id = verified_order_id

    def reply_kind(self) -> str:
        if ToolOutcome.FOUND in self.outcomes:
            return "answer"
        if ToolOutcome.NOT_FOUND in self.outcomes:
            return "not_found"
        if ToolOutcome.NO_MATCH in self.outcomes:
            return "escalation_offer"
        return "clarification"


_current: ContextVar[TurnActivity | None] = ContextVar("turn_activity", default=None)


def current_activity() -> TurnActivity | None:
    return _current.get()


@contextmanager
def begin_activity() -> Generator[TurnActivity]:
    activity = TurnActivity()
    token = _current.set(activity)
    try:
        yield activity
    finally:
        _current.reset(token)
