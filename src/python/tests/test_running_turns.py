"""Codex review on PR #87: a running turn is reported as running, and cancelling one waiter never breaks the turn."""

from __future__ import annotations

import asyncio
import uuid
from typing import Any

import httpx
import pytest

from support_agent.fake_model import ScriptedChatClient
from tests.helpers import assert_contract, host


@pytest.fixture
def anyio_backend() -> str:
    return "asyncio"


@pytest.mark.anyio
async def test_a_running_turn_is_reported_running_and_a_cancelled_duplicate_does_not_break_it() -> None:
    app, _ = host()
    model: ScriptedChatClient = app.state.agents.triage.client
    entered, release = asyncio.Event(), asyncio.Event()

    async def hold() -> None:
        entered.set()
        await release.wait()

    model.before_call = hold
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
        conversation = (await client.post("/api/v1/conversations", json={})).json()["conversation_id"]
        body = {"client_turn_id": str(uuid.uuid4()), "message": "Where is my order ORD-1002?"}
        original = asyncio.create_task(client.post(f"/api/v1/conversations/{conversation}/turns", json=body))
        await asyncio.wait_for(entered.wait(), 5)

        # While it runs: the conversation names it, and a status lookup says "running" (not 404).
        turn_id = (await client.get(f"/api/v1/conversations/{conversation}")).json()["active_turn_id"]
        running = await client.get(f"/api/v1/conversations/{conversation}/turns/{turn_id}")
        assert running.status_code == 200
        assert_contract("turn-result", running.json())
        assert running.json()["state"] == "running"

        # A duplicate waits for the same run; cancelling it must not cancel or invalidate the original.
        duplicate = asyncio.create_task(client.post(f"/api/v1/conversations/{conversation}/turns", json=body))
        await asyncio.sleep(0.05)
        duplicate.cancel()
        with pytest.raises(asyncio.CancelledError):
            await duplicate

        model.before_call = None
        release.set()
        finished: Any = (await asyncio.wait_for(original, 10)).json()
        assert finished["state"] == "finished"
        assert finished["turn_id"] == turn_id
        assert finished["reply"]["kind"] == "answer"

        again = (await client.post(f"/api/v1/conversations/{conversation}/turns", json=body)).json()
        assert again == finished  # later repeats still get the stored result


@pytest.mark.anyio
async def test_a_dropped_request_does_not_stop_its_turn() -> None:
    app, _ = host()
    model: ScriptedChatClient = app.state.agents.triage.client
    entered, release = asyncio.Event(), asyncio.Event()

    async def hold() -> None:
        entered.set()
        await release.wait()

    model.before_call = hold
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
        conversation = (await client.post("/api/v1/conversations", json={})).json()["conversation_id"]
        body = {"client_turn_id": str(uuid.uuid4()), "message": "Where is my order ORD-1002?"}
        dropped = asyncio.create_task(client.post(f"/api/v1/conversations/{conversation}/turns", json=body))
        await asyncio.wait_for(entered.wait(), 5)
        dropped.cancel()  # the client goes away mid-turn
        with pytest.raises(asyncio.CancelledError):
            await dropped

        model.before_call = None
        release.set()
        for _ in range(100):
            state = (await client.get(f"/api/v1/conversations/{conversation}")).json()["active_turn_id"]
            if state is None:
                break
            await asyncio.sleep(0.02)
        replay = (await client.post(f"/api/v1/conversations/{conversation}/turns", json=body)).json()
        assert replay["outcome"] == "completed"
        assert replay["reply"]["kind"] == "answer"
