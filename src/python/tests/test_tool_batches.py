"""Found live: a model may ask for a read and a write in one response. MAF pauses the write, keeps the read in the
session, and runs it when the write is answered, sending both results. The answer must therefore re-send every call of
that batch, or Anthropic rejects the orphaned result (HTTP 400). The fake model enforces Anthropic's pairing rule.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from typing import Any

import pytest
from agent_framework import ChatResponse, Content, Message
from fastapi import FastAPI
from fastapi.testclient import TestClient

from support_agent.fake_model import ScriptedChatClient, default_script
from tests.helpers import Api, host, new_database_path
from tests.test_approvals import decide, state_of


def read_and_write_together(app: FastAPI) -> list[list[tuple[str, list[str]]]]:
    """A model that checks the order and asks to cancel it in the same response. Returns what each call received."""
    model: ScriptedChatClient = app.state.agents.triage.client
    seen: list[list[tuple[str, list[str]]]] = []

    def script(messages: Sequence[Message], options: Mapping[str, Any]) -> ChatResponse:
        seen.append([(m.role, [c.type for c in m.contents]) for m in messages])
        tools: Sequence[Any] = options.get("tools") or []
        names = {str(getattr(tool, "name", "")) for tool in tools}
        asked = any(c.type in ("function_call", "function_result") for m in messages for c in m.contents)
        if "cancel_order" in names and not asked:
            order = "ORD-2002" if "2002" in messages[-1].text else "ORD-1003"
            return ChatResponse(
                messages=[
                    Message(
                        role="assistant",
                        contents=[
                            Content.from_text("Let me check that order and cancel it."),
                            Content.from_function_call(
                                call_id="read_1", name="get_order_status", arguments={"order_id": order}
                            ),
                            Content.from_function_call(
                                call_id="write_1", name="cancel_order", arguments={"order_id": order}
                            ),
                        ],
                    )
                ]
            )
        return default_script(messages, options)

    model.script = script
    return seen


@pytest.mark.parametrize("decision", ["approve", "reject"])
def test_a_write_asked_together_with_a_read_can_be_approved_or_rejected(decision: str) -> None:
    app, _ = host()
    read_and_write_together(app)
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        asked = api.ask(conversation, "Please cancel order ORD-1003.")
        assert asked["outcome"] == "awaiting_approval"
        status, decided = decide(client, conversation, asked["pending_approval"], decision)

    assert status == 200
    assert decided["outcome"] == "completed"
    expected = {"approve": "cancelled", "reject": "processing"}[decision]
    assert state_of(app, "cancel_order") == expected
    if decision == "approve":
        assert decided["reply"]["text"].startswith("Done:")  # the model's own words: its call succeeded (no 400)


def test_a_declined_write_asked_together_with_a_read_still_gets_an_answer() -> None:
    app, _ = host("CUST-1001")
    seen = read_and_write_together(app)
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        turn = api.ask(conversation, "Please cancel order ORD-2002.")  # another customer's order: declined

    assert (turn["outcome"], turn["reply"]["kind"]) == ("completed", "not_found")
    decline_call = seen[-1]
    assert decline_call[-2] == ("assistant", ["function_call", "function_call"])  # the whole batch, re-sent
    assert decline_call[-1][1].count("function_result") == 2


def test_a_mixed_batch_survives_a_restart_before_the_decision() -> None:
    storage = new_database_path()
    before, _ = host(storage=storage)
    read_and_write_together(before)
    with TestClient(before) as client:
        api = Api(client)
        conversation = api.create_conversation()
        pending = api.ask(conversation, "Please cancel order ORD-1003.")["pending_approval"]

    after, _ = host(storage=storage)
    read_and_write_together(after)
    with TestClient(after) as client:
        status, decided = decide(client, conversation, pending, "approve")
    assert (status, decided["outcome"]) == (200, "completed")
    assert decided["reply"]["text"].startswith("Done:")
    assert state_of(after, "cancel_order") == "cancelled"
