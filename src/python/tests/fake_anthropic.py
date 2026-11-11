"""A fake Anthropic HTTP API under the real Anthropic SDK (L42, like .NET's FakeAnthropicHttp).

Tests script what each HTTP attempt gets (a status, headers, a delay, a connection failure), so the SDK's real retry
loop, backoff, ``retry-after`` handling and attempt timeouts run deterministically, with no network and no key. Every
attempt is recorded: whether it was triage (a structured-output request) and when it arrived.
"""

from __future__ import annotations

import asyncio
import json
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import anthropic
import httpx2

from support_agent.settings import (
    AnthropicSettings,
    Environment,
    IdentitySettings,
    ModelMode,
    ModelProviderSettings,
    ResilienceSettings,
    Settings,
    StorageSettings,
)
from tests.helpers import new_database_path

TRIAGE_ORDERS = {
    "target": "orders",
    "requested_action": "order_status",
    "order_id": "ORD-1002",
    "summary": "Order status question.",
    "customer_sentiment": "neutral",
    "confidence": 0.9,
}
REPLY = "Order ORD-1002 is on its way."


@dataclass(frozen=True, slots=True)
class Attempt:
    triage: bool
    streamed: bool
    at: float


@dataclass(frozen=True, slots=True)
class Fault:
    """What one attempt gets instead of a normal answer."""

    status: int = 500
    headers: dict[str, str] = field(default_factory=dict[str, str])
    delay: float = 0.0  # seconds before answering (longer than the attempt timeout: the SDK times out)
    connection_error: bool = False


Plan = Callable[[Attempt, int], Fault | None]  # (this attempt, its 1-based number) -> a fault, or None to answer


def message(text: str) -> dict[str, Any]:
    return {
        "id": "msg_fake",
        "type": "message",
        "role": "assistant",
        "model": "claude-haiku-5-5",
        "content": [{"type": "text", "text": text}],
        "stop_reason": "end_turn",
        "stop_sequence": None,
        "usage": {"input_tokens": 10, "output_tokens": 5},
    }


def sse(text: str) -> bytes:
    start: dict[str, Any] = {**message(""), "content": []}
    events: list[tuple[str, dict[str, Any]]] = [
        ("message_start", {"type": "message_start", "message": start}),
        (
            "content_block_start",
            {"type": "content_block_start", "index": 0, "content_block": {"type": "text", "text": ""}},
        ),
        (
            "content_block_delta",
            {"type": "content_block_delta", "index": 0, "delta": {"type": "text_delta", "text": text}},
        ),
        ("content_block_stop", {"type": "content_block_stop", "index": 0}),
        (
            "message_delta",
            {
                "type": "message_delta",
                "delta": {"stop_reason": "end_turn", "stop_sequence": None},
                "usage": {"output_tokens": 5},
            },
        ),
        ("message_stop", {"type": "message_stop"}),
    ]
    return "".join(f"event: {name}\ndata: {json.dumps(data)}\n\n" for name, data in events).encode()


class FakeAnthropicHttp:
    def __init__(self, plan: Plan | None = None) -> None:
        self.plan: Plan = plan or (lambda _attempt, _number: None)
        self.attempts: list[Attempt] = []
        self.on_attempt: Callable[[Attempt], Awaitable[None]] | None = None
        self.client = anthropic.DefaultAsyncHttpxClient(transport=httpx2.MockTransport(self._handle))

    @property
    def triage_attempts(self) -> list[Attempt]:
        return [attempt for attempt in self.attempts if attempt.triage]

    @property
    def specialist_attempts(self) -> list[Attempt]:
        return [attempt for attempt in self.attempts if not attempt.triage]

    async def _handle(self, request: httpx2.Request) -> httpx2.Response:
        body: dict[str, Any] = json.loads(await request.aread() or b"{}")
        attempt = Attempt(triage="output_config" in body, streamed=bool(body.get("stream")), at=time.monotonic())
        self.attempts.append(attempt)
        if self.on_attempt is not None:
            await self.on_attempt(attempt)
        fault = self.plan(attempt, len(self.attempts))
        if fault is not None:
            if fault.delay:
                # A real transport enforces the attempt's read timeout; MockTransport doesn't, so do it here.
                timeouts: dict[str, float | None] = request.extensions.get("timeout", {})
                limit = timeouts.get("read")
                if limit is not None and fault.delay > limit:
                    await asyncio.sleep(limit)
                    raise httpx2.ReadTimeout("timed out", request=request)
                await asyncio.sleep(fault.delay)
            if fault.connection_error:
                raise httpx2.ConnectError("connection refused", request=request)
            error = {"type": "error", "error": {"type": "api_error", "message": "fake failure"}}
            return httpx2.Response(fault.status, headers={"x-should-retry": "", **fault.headers}, json=error)
        if attempt.triage:
            return httpx2.Response(200, json=message(json.dumps(TRIAGE_ORDERS)))
        if attempt.streamed:
            return httpx2.Response(200, headers={"content-type": "text/event-stream"}, content=sse(REPLY))
        return httpx2.Response(200, json=message(REPLY))


def live_settings(storage: Path | None = None, **limits: Any) -> Settings:
    """Live mode (the real SDK) with a fake key: the fake HTTP API answers, nothing leaves the process."""
    return Settings(
        app_environment=Environment.DEVELOPMENT,
        identity=IdentitySettings(development_customer_id="CUST-1001"),
        storage=StorageSettings(path=storage or new_database_path()),
        model_provider=ModelProviderSettings(
            mode=ModelMode.LIVE,
            anthropic=AnthropicSettings(model="claude-haiku-5-5", api_key="sk-fake-not-a-key"),  # pyright: ignore[reportArgumentType]
        ),
        resilience=ResilienceSettings(**limits),
    )
