"""Bounded turns (the Python counterpart of .NET's resilience): one retry owner, per-turn budgets, tool limits, and classified failures.

Retry inventory (docs/observability.md, "Failures, retries and budgets"):

- **HTTP to Anthropic:** the Anthropic SDK is the **only** retry owner (``RESILIENCE__MODEL_MAX_RETRIES``, honouring the
  provider's ``retry-after``). Its HTTP transport has no retries of its own (httpx's default), and nothing above it
  retries a model call.
- **Model calls in a turn:** never retried; counted against ``max_model_calls_per_turn`` (``ModelCallBudget``).
- **Tools:** never retried. Each call has a time limit, and a tool fault ends the turn (``ToolLimits``) instead of being
  handed back to the model, which would invite a retry loop. MAF's tool loop is capped at ``max_tool_iterations``.
- **The turn:** never retried; it has a deadline. A client may retry with a new ``client_turn_id``.
- **Writes:** never retried blindly. Only the operation state machine resumes one, and its guarded, atomic
  commit means it can't apply twice.
"""

from __future__ import annotations

import asyncio
import email.utils
import math
import time
from collections.abc import Awaitable, Callable, Generator, Iterator
from contextlib import contextmanager
from contextvars import ContextVar
from dataclasses import dataclass
from typing import Any, cast

import anthropic
from agent_framework import (
    ChatContext,
    ChatMiddleware,
    FunctionInvocationContext,
    FunctionMiddleware,
    MiddlewareFailure,
)

# MAF's own argument-validation error (raised before a tool body starts). It has no public name in 1.25.0, so it is
# imported explicitly: if a release renames it, this import fails at startup instead of silently widening the exemption.
from agent_framework._tools import _FunctionArgumentValidationError  # pyright: ignore[reportPrivateUsage]


@dataclass(frozen=True, slots=True)
class FailureCode:
    code: str
    detail: str
    retry_after_seconds: int | None = None


class BudgetExhaustedError(RuntimeError):
    """The turn used up its model-call budget. The turn ends failed/budget_exhausted."""


class ToolUnavailableError(MiddlewareFailure):
    """A tool overran its time limit or failed. A ``MiddlewareFailure``: MAF's fail-closed signal, which ends the run
    instead of becoming an error result the model could retry. The turn ends failed/tool_unavailable."""


@dataclass(eq=False)
class TurnBudget:
    """One turn's running budget: how many model calls it made, and how many HTTP attempts those took."""

    max_model_calls: int
    model_calls: int = 0
    model_attempts: int = 0

    def consume_model_call(self) -> None:
        self.model_calls += 1
        if self.model_calls > self.max_model_calls:
            raise BudgetExhaustedError(f"The turn used its budget of {self.max_model_calls} model calls.")

    def count_attempt(self) -> None:
        self.model_attempts += 1


_budget: ContextVar[TurnBudget | None] = ContextVar("turn_budget", default=None)


@contextmanager
def budgeting(max_model_calls: int) -> Generator[TurnBudget]:
    """Gives the code (and tasks) started inside one budget. Model calls outside a turn (DevUI) have no turn budget;
    they keep the per-call limits."""
    budget = TurnBudget(max_model_calls)
    token = _budget.set(budget)
    try:
        yield budget
    finally:
        _budget.reset(token)


def current_budget() -> TurnBudget | None:
    return _budget.get()


class ModelCallBudget(ChatMiddleware):
    """Counts every model call against the running turn's budget **before** it is made: a call over budget never
    reaches the provider (or its telemetry). A model that loops (calling tools forever) runs out of budget instead of
    spending without limit."""

    async def process(self, context: ChatContext, call_next: Callable[[], Awaitable[None]]) -> None:
        if (budget := _budget.get()) is not None:
            budget.consume_model_call()
        await call_next()


class ToolLimits(FunctionMiddleware):
    """A time limit per tool call, and a tool fault ends the turn instead of going back to the model.

    Argument validation errors are not faults: MAF raises them before the tool starts, and the model may correct its
    arguments (bounded by the tool loop and the turn budget). Write tools report business refusals as results, never
    as exceptions, so a refusal is never turned into ``tool_unavailable``."""

    def __init__(self, timeout_seconds: float) -> None:
        self.timeout_seconds = timeout_seconds

    async def process(self, context: FunctionInvocationContext, call_next: Callable[[], Awaitable[None]]) -> None:
        name = context.function.name
        try:
            async with asyncio.timeout(self.timeout_seconds):
                await call_next()
        except TimeoutError as error:
            raise ToolUnavailableError(f"The tool {name} did not finish within {self.timeout_seconds:g} s.") from error
        except MiddlewareFailure, _FunctionArgumentValidationError:
            raise  # already fail-closed, or MAF's argument validation (for the model to correct); nothing else
        except Exception as error:
            raise ToolUnavailableError(f"The tool {name} failed.") from error


async def count_attempts(request: Any, call_next: Callable[[Any], Awaitable[Any]]) -> Any:
    """Anthropic SDK middleware: it runs once per HTTP attempt, inside the SDK's retry loop, so retries are visible per
    turn. It never retries anything itself."""
    if (budget := _budget.get()) is not None:
        budget.count_attempt()
    return await call_next(request)


# ---- Classification -------------------------------------------------------------------------------------------------

TIMEOUT = FailureCode("timeout", "This took too long, so it was stopped. Please try again.")
BUDGET = FailureCode(
    "budget_exhausted", "This request needed more steps than allowed, so it was stopped. Please rephrase or try again."
)
TOOL = FailureCode("tool_unavailable", "A lookup did not respond in time. Please try again shortly.")
MODEL_TIMEOUT = FailureCode("timeout", "The assistant took too long to answer. Please try again.")
UNAVAILABLE = FailureCode("model_unavailable", "The assistant is temporarily unavailable. Please try again shortly.")
INTERNAL = FailureCode("internal_error", "Something went wrong while handling your message. Please try again.")


def chain(error: BaseException | None) -> Iterator[BaseException]:
    """The error and everything behind it: causes, contexts, wrapped errors, and exception groups."""
    seen: set[int] = set()
    pending: list[BaseException] = [error] if error is not None else []
    while pending:
        current: BaseException = pending.pop()
        if id(current) in seen:
            continue
        seen.add(id(current))
        yield current
        if isinstance(current, BaseExceptionGroup):
            group = cast(BaseExceptionGroup[BaseException], current)
            pending.extend(group.exceptions)
        # MAF's exceptions keep the wrapped error in their arguments (``AgentFrameworkException(message, inner)``).
        for behind in (current.__cause__, current.__context__, *current.args):
            if isinstance(behind, BaseException):
                pending.append(behind)


def retry_after_seconds(error: anthropic.APIStatusError) -> int | None:
    """The provider's retry hint (``retry-after-ms`` or ``retry-after``, in seconds or as a date), rounded up."""
    headers = error.response.headers
    seconds: float | None = None
    if (milliseconds := headers.get("retry-after-ms")) is not None:
        try:
            seconds = float(milliseconds) / 1000
        except ValueError:
            seconds = None
    if seconds is None and (value := headers.get("retry-after")) is not None:
        try:
            seconds = float(value)
        except ValueError:
            parsed = email.utils.parsedate_tz(value)
            seconds = None if parsed is None else email.utils.mktime_tz(parsed) - time.time()
    return None if seconds is None else max(1, math.ceil(seconds))


def classify(error: BaseException, *, turn_timed_out: bool = False) -> FailureCode:
    """Maps a turn's failure to the contract's ``turn_error``. The detail is fixed, user-safe text: never the exception
    message, which may hold provider payloads or internals (that goes to the log and the trace). Transient provider
    faults were already retried by the SDK before they get here; nothing in the application retries a turn."""
    if turn_timed_out:
        return TIMEOUT
    errors = list(chain(error))
    if any(isinstance(e, BudgetExhaustedError) for e in errors):
        return BUDGET
    if any(isinstance(e, ToolUnavailableError) for e in errors):
        return TOOL
    if limited := next((e for e in errors if isinstance(e, anthropic.RateLimitError)), None):
        hint = retry_after_seconds(limited)
        return FailureCode("rate_limited", "The assistant is busy right now. Please try again shortly.", hint)
    if any(isinstance(e, anthropic.APITimeoutError) for e in errors):
        return MODEL_TIMEOUT
    # Faults the SDK already retried (5xx, overloaded, connection) mean the provider is unavailable. Other provider
    # errors (400, 401, 403, 404, 413, 422) are permanent: retrying can't help, so they are internal errors.
    if any(
        isinstance(e, anthropic.APIConnectionError)
        or (isinstance(e, anthropic.APIStatusError) and e.status_code >= 500)
        for e in errors
    ):
        return UNAVAILABLE
    return INTERNAL
