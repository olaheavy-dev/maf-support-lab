"""Per-turn control (the Python counterpart of .NET's turn control): cooperative cancellation and the live events a stream shows.

A turn runs as its own task, detached from any request. Its ``TurnControl`` is the only way to stop it: a cancel request
sets the flag and cancels the task, so an in-flight model call stops at its next await. Work also checks the flag at
the pre-commit boundary (``OperationService.execute_approved``), so once cancellation is seen, no write starts. A
dropped connection never touches the control: the turn runs on and is stored.

Live events (progress and reply text) go to an observer only while a streaming request is attached. Emitting never
blocks the turn and never fails it.
"""

from __future__ import annotations

import asyncio
from collections.abc import Generator
from contextlib import contextmanager
from contextvars import ContextVar
from dataclasses import dataclass, field
from typing import Any


class TurnCancelled(asyncio.CancelledError):
    """Raised at a cancellation boundary once the turn's cancellation was requested.

    A ``CancelledError``, so it means what asyncio cancellation means everywhere: MAF's tool layer turns ordinary
    exceptions into an error result for the model (and the run would continue), but lets cancellation through."""


@dataclass(frozen=True, slots=True)
class Progress:
    """A progress step: a stage, maybe a route or a tool name. Never message content."""

    stage: str
    route: dict[str, Any] | None = None
    tool: str | None = None


@dataclass(frozen=True, slots=True)
class Delta:
    """Reply text as it arrives (display only; the stored turn is authoritative)."""

    text: str


@dataclass(eq=False)
class TurnControl:
    cancel_requested: asyncio.Event = field(default_factory=asyncio.Event)
    task: asyncio.Task[None] | None = None
    observer: asyncio.Queue[Progress | Delta] | None = None
    # Set by the turn as its first step, inside its own cancellation handler (Codex review on PR #92).
    started: bool = False

    def request_cancel(self) -> None:
        """Sets the flag and interrupts the task, but only once it has started: cancelling a task that has not run yet
        would skip its body, and with it the handler that stores the cancelled outcome. A turn that has not started
        sees the flag at its first boundary instead."""
        self.cancel_requested.set()
        if self.started and self.task is not None and not self.task.done():
            self.task.cancel()

    @property
    def is_cancel_requested(self) -> bool:
        return self.cancel_requested.is_set()

    def emit(self, item: Progress | Delta) -> None:
        if self.observer is not None and (not isinstance(item, Delta) or item.text):
            self.observer.put_nowait(item)


_current: ContextVar[TurnControl | None] = ContextVar("turn_control", default=None)


@contextmanager
def controlling(control: TurnControl) -> Generator[TurnControl]:
    """Makes ``control`` the current turn's control for the code (and tasks) started inside."""
    token = _current.set(control)
    try:
        yield control
    finally:
        _current.reset(token)


def current() -> TurnControl | None:
    return _current.get()


def raise_if_cancelled() -> None:
    """A cancellation boundary: past this point the turn starts nothing new once cancellation was requested."""
    if (control := _current.get()) is not None and control.is_cancel_requested:
        raise TurnCancelled


def emit(item: Progress | Delta) -> None:
    if (control := _current.get()) is not None:
        control.emit(item)
