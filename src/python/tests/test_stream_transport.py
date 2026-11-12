"""What a real HTTP connection sees. These tests run the app in Uvicorn on a loopback port, because an in-process
ASGI test transport collects the whole body first and so could neither show buffering nor really disconnect.

- Reply text reaches the client while the turn is still running (the stream is not buffered).
- A client that disconnects mid-stream stops receiving, but its turn runs on: the write commits once, and the status
  lookup and a repeated decision report the stored outcome (the stretch: disconnect while a slow tool runs).
- A cancel request ends a live stream with the cancelled result, after withdrawing the text already streamed.
"""

from __future__ import annotations

import asyncio
import json
import uuid
from collections.abc import AsyncGenerator
from typing import Any

import httpx
import pytest
import uvicorn
from fastapi import FastAPI

from support_agent.fake_model import ScriptedChatClient
from support_agent.operations import Status
from tests.helpers import SSE, assert_contract, host
from tests.test_approvals import AFTER, state_of


@pytest.fixture
def anyio_backend() -> str:
    return "asyncio"


@pytest.fixture
async def served() -> AsyncGenerator[tuple[FastAPI, httpx.AsyncClient]]:
    app, _ = host()
    server = uvicorn.Server(uvicorn.Config(app, host="127.0.0.1", port=0, log_level="warning", lifespan="on"))
    serving = asyncio.create_task(server.serve())
    for _ in range(200):
        if server.started:
            break
        await asyncio.sleep(0.01)
    port = server.servers[0].sockets[0].getsockname()[1]
    async with httpx.AsyncClient(base_url=f"http://127.0.0.1:{port}", timeout=10) as client:
        yield app, client
    server.should_exit = True
    await asyncio.wait_for(serving, 10)


async def events_of(response: httpx.Response) -> AsyncGenerator[dict[str, Any]]:
    """Parses SSE events as they arrive on the wire, checking each against the contract."""
    buffer = ""
    async for chunk in response.aiter_text():
        buffer += chunk
        while "\n\n" in buffer:
            block, buffer = buffer.split("\n\n", 1)
            fields = dict(line.split(": ", 1) for line in block.splitlines() if line and not line.startswith(":"))
            if fields:
                event: dict[str, Any] = json.loads(fields["data"])
                assert_contract("stream-event", event)
                yield event


def model(app: FastAPI) -> ScriptedChatClient:
    return app.state.agents.triage.client


@pytest.mark.anyio
async def test_reply_text_reaches_the_client_while_the_turn_is_still_running(
    served: tuple[FastAPI, httpx.AsyncClient],
) -> None:
    app, client = served
    held, release = asyncio.Event(), asyncio.Event()

    async def after_first_word() -> None:
        held.set()
        await release.wait()

    conversation = (await client.post("/api/v1/conversations", json={})).json()["conversation_id"]
    model(app).between_updates = after_first_word
    body = {"client_turn_id": str(uuid.uuid4()), "message": "Where is my order ORD-1002?"}
    seen: list[str] = []
    async with client.stream("POST", f"/api/v1/conversations/{conversation}/turns", json=body, headers=SSE) as response:
        async for event in events_of(response):
            seen.append(event["type"])
            if event["type"] == "reply.delta" and not release.is_set():
                # The first word is on the wire; the model is held, so the turn cannot have finished.
                assert held.is_set()
                status = await client.get(f"/api/v1/conversations/{conversation}/turns/{event['turn_id']}")
                assert status.json()["state"] == "running"
                model(app).between_updates = None
                release.set()
    assert seen[0] == "turn.started"
    assert seen[-1] == "turn.final"
    assert seen.count("reply.delta") > 1


@pytest.mark.anyio
async def test_a_client_that_disconnects_during_a_slow_write_loses_only_the_stream(
    served: tuple[FastAPI, httpx.AsyncClient],
) -> None:
    app, client = served
    slow_write, finish = asyncio.Event(), asyncio.Event()

    async def slow_tool() -> None:
        slow_write.set()
        await finish.wait()

    conversation = (await client.post("/api/v1/conversations", json={})).json()["conversation_id"]
    asked = (
        await client.post(
            f"/api/v1/conversations/{conversation}/turns",
            json={"client_turn_id": str(uuid.uuid4()), "message": "Please cancel order ORD-1003."},
        )
    ).json()
    pending = asked["pending_approval"]
    app.state.operations.before_commit = slow_tool
    decision = {"operation_id": pending["operation_id"], "decision": "approve"}
    url = f"/api/v1/conversations/{conversation}/approvals/{pending['approval_id']}"

    turn_id = ""
    async with client.stream("POST", url, json=decision, headers=SSE) as response:
        async for event in events_of(response):
            turn_id = event["turn_id"]
            if event["type"] == "turn.progress" and event["data"]["stage"] == "executing_operation":
                break  # the client goes away mid-write, before any final event
    await asyncio.wait_for(slow_write.wait(), 5)
    # Wait until the server has noticed the disconnect and detached the stream, while the write is still held: only
    # then does finishing the write show that the disconnect itself did not stop the turn.
    claim = app.state.conversations._conversations[conversation].running(turn_id)
    assert claim is not None
    for _ in range(250):
        if claim.control.observer is None:
            break
        await asyncio.sleep(0.02)
    assert claim.control.observer is None
    assert not claim.done.done()

    app.state.operations.before_commit = None
    finish.set()  # the slow write now completes, with nobody listening
    status: dict[str, Any] = {}
    for _ in range(200):
        status = (await client.get(f"/api/v1/conversations/{conversation}/turns/{turn_id}")).json()
        if status["state"] == "finished":
            break
        await asyncio.sleep(0.02)

    # Reconciled through the status lookups: the write happened once, and the turn reports it.
    assert_contract("turn-result", status)
    assert (status["outcome"], status["reply"]["kind"]) == ("completed", "approval_result")
    operation = (await client.get(f"/api/v1/operations/{pending['operation_id']}")).json()
    assert operation["status"] == "committed"
    assert state_of(app, "cancel_order") == AFTER["cancel_order"]
    replay = (await client.post(url, json=decision)).json()
    assert replay["turn_id"] == turn_id
    assert app.state.operations.store.find(pending["operation_id"]).status is Status.COMMITTED


@pytest.mark.anyio
async def test_cancelling_a_streamed_turn_withdraws_its_text_and_ends_with_the_cancelled_result(
    served: tuple[FastAPI, httpx.AsyncClient],
) -> None:
    app, client = served
    hold = asyncio.Event()

    async def stall() -> None:
        await hold.wait()  # never released: only the cancellation ends this turn

    conversation = (await client.post("/api/v1/conversations", json={})).json()["conversation_id"]
    model(app).between_updates = stall
    body = {"client_turn_id": str(uuid.uuid4()), "message": "Where is my order ORD-1002?"}
    events: list[dict[str, Any]] = []
    async with client.stream("POST", f"/api/v1/conversations/{conversation}/turns", json=body, headers=SSE) as response:
        async for event in events_of(response):
            events.append(event)
            if event["type"] == "reply.delta":
                cancelled = await client.post(f"/api/v1/conversations/{conversation}/turns/{event['turn_id']}/cancel")
                assert cancelled.status_code == 202

    types = [e["type"] for e in events]
    assert types[-2:] == ["reply.reset", "turn.final"]
    result = events[-1]["data"]["result"]
    assert result["outcome"] == "cancelled"
    assert "reply" not in result
