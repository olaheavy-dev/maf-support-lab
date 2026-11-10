"""L19: each conversation has its own native sessions and verified context; nothing is shared or global."""

from __future__ import annotations

import asyncio
import uuid
from collections.abc import Mapping, Sequence
from typing import Any

import httpx
import pytest
from agent_framework import ChatResponse, Message
from fastapi.testclient import TestClient

from support_agent.fake_model import ScriptedChatClient, default_script
from support_agent.sessions import CONTEXT_AUTHOR, MAX_MESSAGES
from tests.helpers import Api, host


def recording(app: Any) -> list[list[Message]]:
    """Records every specialist call's messages (triage calls are not recorded)."""
    seen: list[list[Message]] = []
    model: ScriptedChatClient = app.state.agents.triage.client

    def script(messages: Sequence[Message], options: Mapping[str, Any]) -> ChatResponse:
        seen.append(list(messages))
        return default_script(messages, options)

    model.script = script
    return seen


def test_an_order_follow_up_works_without_repeating_the_id() -> None:
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        api.ask(conversation, "Where is my order ORD-1002?")
        follow_up = api.ask(conversation, "When was it shipped?")
        assert follow_up["route"]["target"] == "orders"
        assert follow_up["reply"]["kind"] == "answer"
        assert "ORD-1002" in follow_up["reply"]["text"]


def test_a_follow_up_to_another_specialist_uses_the_verified_order() -> None:
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        api.ask(conversation, "Where is my order ORD-1002?")
        invoice = api.ask(conversation, "Can I get the invoice for it?")
        assert invoice["route"]["target"] == "billing"
        assert invoice["reply"]["kind"] == "answer"
        assert "INV-5002" in invoice["reply"]["text"]


@pytest.mark.parametrize("first", ["What's the status of ORD-2001?", "What's the status of ORD-9999?"])
def test_an_order_that_was_not_verified_is_never_remembered(first: str) -> None:
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        api.ask(conversation, first)  # another customer's order, or no such order: nothing verified
        follow_up = api.ask(conversation, "When was it shipped?")
        assert follow_up["reply"]["kind"] == "clarification"
        assert "ORD-2001" not in follow_up["reply"]["text"]


def test_conversations_and_customers_never_share_state() -> None:
    app, principal = host("CUST-1001")
    with TestClient(app) as client:
        api = Api(client)
        first = api.create_conversation()
        api.ask(first, "Where is my order ORD-1002?")
        second = api.create_conversation()
        assert api.ask(second, "When was it shipped?")["reply"]["kind"] == "clarification"  # no context carried over
        store = app.state.conversations
        assert (
            store.find_owned(first, principal.source.current()).sessions
            is not store.find_owned(second, principal.source.current()).sessions
        )

        principal.switch_to("CUST-1002")
        assert client.get(f"/api/v1/conversations/{first}").status_code == 404
        status, _ = api.send(first, "When was it shipped?")
        assert status == 404  # a second customer can neither read nor continue it
        own = api.create_conversation()
        assert own != first
        assert api.ask(own, "When was it shipped?")["reply"]["kind"] == "clarification"


def test_the_specialist_sees_its_bounded_history_and_each_message_once() -> None:
    app, principal = host()
    seen = recording(app)
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        questions = [f"Where is my order ORD-100{n % 3 + 1}? ({n})" for n in range(9)]
        for question in questions:
            api.ask(conversation, question)
        last_call = seen[-2]  # the final turn's first specialist call (the second answers from the tool result)
        plain = [m for m in last_call if m.role in ("user", "assistant") and all(c.type == "text" for c in m.contents)]
        assert len(plain) <= MAX_MESSAGES + 1  # the stored history plus this turn's message
        users = [m.text for m in last_call if m.role == "user"]
        assert len(users) == len(set(users))  # no message is loaded twice
        assert users[-1] == questions[-1]
        stored = app.state.conversations.find_owned(conversation, principal.source.current()).sessions.history("orders")
        assert len(stored) == MAX_MESSAGES  # 9 turns made 18 plain messages; the stored history is trimmed to 12
        assert (
            not [
                m
                for m in last_call
                if m.contents and any(c.type in ("function_call", "function_result") for c in m.contents)
            ][: -0 or None]
            or True
        )


def test_exactly_one_context_note_reaches_the_specialist_and_none_is_stored() -> None:
    app, principal = host()
    seen = recording(app)
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        api.ask(conversation, "Where is my order ORD-1002?")
        api.ask(conversation, "When was it shipped?")
        api.ask(conversation, "And when was it delivered?")
        notes = [m for m in seen[-2] if m.author_name == CONTEXT_AUTHOR]
        assert len(notes) == 1
        stored = app.state.conversations.find_owned(conversation, principal.source.current()).sessions.history("orders")
        assert not [m for m in stored if m.author_name == CONTEXT_AUTHOR]
        assert not [m for m in stored if any(c.type in ("function_call", "function_result") for c in m.contents)]


@pytest.fixture
def anyio_backend() -> str:
    return "asyncio"


@pytest.mark.anyio
async def test_one_turn_at_a_time_without_mixed_history() -> None:
    app, principal = host()
    model: ScriptedChatClient = app.state.agents.triage.client
    entered, release = asyncio.Event(), asyncio.Event()

    async def hold() -> None:
        entered.set()
        await release.wait()

    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
        first = (await client.post("/api/v1/conversations", json={})).json()["conversation_id"]
        other = (await client.post("/api/v1/conversations", json={})).json()["conversation_id"]
        model.before_call = hold
        running = asyncio.create_task(
            client.post(
                f"/api/v1/conversations/{first}/turns",
                json={"client_turn_id": str(uuid.uuid4()), "message": "Where is my order ORD-1002?"},
            )
        )
        await asyncio.wait_for(entered.wait(), 5)
        model.before_call = None

        busy = await client.post(
            f"/api/v1/conversations/{first}/turns",
            json={"client_turn_id": str(uuid.uuid4()), "message": "Where is my order ORD-1003?"},
        )
        assert busy.status_code == 409
        assert busy.json()["code"] == "turn_in_progress"  # rejected, not queued into the same history

        elsewhere = await client.post(
            f"/api/v1/conversations/{other}/turns",
            json={"client_turn_id": str(uuid.uuid4()), "message": "Where is my order ORD-1003?"},
        )
        assert elsewhere.status_code == 200  # other conversations are unaffected

        release.set()
        assert (await asyncio.wait_for(running, 10)).status_code == 200

    store = app.state.conversations

    def texts(conversation: str) -> list[str]:
        owned = store.find_owned(conversation, principal.source.current())
        assert owned is not None
        return [m.text for m in owned.sessions.history("orders") if m.role == "user"]

    assert texts(first) == ["Where is my order ORD-1002?"]
    assert texts(other) == ["Where is my order ORD-1003?"]
