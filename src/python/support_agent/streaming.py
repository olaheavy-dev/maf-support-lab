"""Server-Sent Events for a turn (contract 1.1 streaming; L20, porting L12's TurnStream).

Asked for with ``Accept: text/event-stream`` on the turn and approval routes. The stream is:
- ``turn.started``;
- the turn's live ``turn.progress`` and ``reply.delta`` events (only for the request that started the turn);
- one terminal ``turn.final`` carrying the stored turn result.

Errors found before the turn starts are ordinary problem responses. Reply text is the model's tokens as they arrive.
When the reply the server settles on differs from what was streamed (an approval request, a declined write, a failure,
or a cancellation), a ``reply.reset`` tells the client to discard it, and the final text follows.

Buffering is explicit: nothing is buffered here, and the response asks proxies not to buffer either. A client that
disconnects only stops receiving: the generator detaches from the turn, and the turn runs on and is stored.
"""

from __future__ import annotations

import asyncio
import json
from collections.abc import AsyncGenerator, Callable
from datetime import UTC, datetime
from typing import Any

from fastapi import Request
from fastapi.sse import EventSourceResponse

from support_agent import contract
from support_agent.conversations import Claim, Turn
from support_agent.telemetry import first_delta
from support_agent.turn_control import Delta, Progress

MAX_DELTA_LENGTH = 2000  # the contract's limit for one reply.delta (code points, as JSON Schema counts them)
KEEPALIVE_SECONDS = 15.0
KEEPALIVE = ": keepalive\n\n"  # the contract's comment line (FastAPI's own constant says "ping")
HEADERS = {"Cache-Control": "no-cache", "X-Accel-Buffering": "no"}


def is_requested(request: Request) -> bool:
    return "text/event-stream" in request.headers.get("accept", "").lower()


def pieces(text: str) -> list[str]:
    """Splits text into contract-sized deltas. Python strings are code points, so a piece never splits a character."""
    return [text[start : start + MAX_DELTA_LENGTH] for start in range(0, len(text), MAX_DELTA_LENGTH)]


def respond(
    claim: Claim,
    conversation_id: str,
    started: dict[str, Any],
    live: bool,
    final: Callable[[Turn | None], dict[str, Any]],
) -> EventSourceResponse:
    """The SSE response for one claimed turn. ``live`` only for the request that started it (it gets the deltas)."""
    return EventSourceResponse(_events(claim, conversation_id, started, live, final), headers=HEADERS)


async def _events(
    claim: Claim,
    conversation_id: str,
    started: dict[str, Any],
    live: bool,
    final: Callable[[Turn | None], dict[str, Any]],
) -> AsyncGenerator[str]:
    sequence = 0
    streamed: list[str] = []

    def event(kind: str, data: dict[str, Any]) -> str:
        nonlocal sequence
        sequence += 1
        body = {
            "contract_version": contract.CONTRACT_VERSION,
            "type": kind,
            "conversation_id": conversation_id,
            "turn_id": claim.turn_id,
            "sequence": sequence,
            "data": data,
        }
        return f"id: {sequence}\nevent: {kind}\ndata: {json.dumps(body, ensure_ascii=False)}\n\n"

    queue = claim.control.observer if live else None
    try:
        yield event("turn.started", started)
        if queue is not None:
            async for item in _live(queue, claim.done):
                if isinstance(item, str):
                    yield item  # a keepalive comment
                elif isinstance(item, Progress):
                    yield event("turn.progress", _progress(item))
                else:
                    if not streamed:  # the first reply text of a live stream (L20's latency, measured: L40)
                        first_delta.record((datetime.now(UTC) - claim.started_at).total_seconds())
                    streamed.append(item.text)
                    for piece in pieces(item.text):
                        yield event("reply.delta", {"text": piece})

        result = final(await _stored(claim))
        # The streamed text must add up to the final reply: if it doesn't, reset, then send the reply itself.
        reply = str(result.get("reply", {}).get("text", ""))
        if "".join(streamed) != reply:
            if streamed:
                yield event("reply.reset", {})
            for piece in pieces(reply):
                yield event("reply.delta", {"text": piece})
        yield event("turn.final", {"result": result})
    finally:
        if live:
            claim.control.observer = None  # detached: a turn whose client left emits nothing more


async def _live(
    queue: asyncio.Queue[Progress | Delta], done: asyncio.Future[Turn]
) -> AsyncGenerator[Progress | Delta | str]:
    """The turn's events as they happen, until it finishes; a keepalive comment when nothing happened for a while."""
    while not done.done():
        getting = asyncio.ensure_future(queue.get())
        try:
            finished, _ = await asyncio.wait(
                {getting, done}, timeout=KEEPALIVE_SECONDS, return_when=asyncio.FIRST_COMPLETED
            )
        finally:
            if not getting.done():
                getting.cancel()
        if getting in finished:
            yield getting.result()
        elif not finished:
            yield KEEPALIVE
    while not queue.empty():  # everything emitted before the turn finished
        yield queue.get_nowait()


async def _stored(claim: Claim) -> Turn | None:
    """The stored turn; None if it could not be stored (the stream then ends with a failed result, never success)."""
    try:
        return await asyncio.shield(claim.done)
    except Exception:
        return None


def _progress(item: Progress) -> dict[str, Any]:
    data: dict[str, Any] = {"stage": item.stage}
    if item.route is not None:
        data["route"] = item.route
    if item.tool is not None:
        data["tool"] = item.tool
    return data
