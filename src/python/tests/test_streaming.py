"""L20: turns stream as Server-Sent Events (contract 1.1), porting .NET L12's StreamingTests.

Every event is checked against ``stream-event.schema.json`` as it is parsed. A stream is ``turn.started``, then
progress and reply text, then exactly one ``turn.final`` carrying the stored turn. The deltas after the last
``reply.reset`` always add up to the final reply text.
"""

from __future__ import annotations

import uuid
from collections.abc import Mapping, Sequence
from typing import Any

import pytest
from agent_framework import ChatResponse, Content, Message
from fastapi.testclient import TestClient

from support_agent import streaming
from support_agent.fake_model import ScriptedChatClient, default_script, text, tool_call
from tests.helpers import SSE, Api, assert_contract, final_of, host, parse_sse, stages, streamed_text
from tests.test_approvals import state_of


def stream_turn(client: TestClient, conversation: str, message: str, client_turn_id: str | None = None) -> Any:
    return client.post(
        f"/api/v1/conversations/{conversation}/turns",
        json={"client_turn_id": client_turn_id or str(uuid.uuid4()), "message": message},
        headers=SSE,
    )


def tool_names(options: Mapping[str, Any]) -> set[str]:
    tools: Sequence[Any] = options.get("tools") or []
    return {str(getattr(tool, "name", "")) for tool in tools}


def model(app: Any) -> ScriptedChatClient:
    return app.state.agents.triage.client


def test_an_answer_streams_progress_and_token_deltas_then_one_final_result() -> None:
    app, _ = host()
    with TestClient(app) as client:
        conversation = Api(client).create_conversation()
        response = stream_turn(client, conversation, "Where is my order ORD-1002?")

        assert response.status_code == 200
        assert response.headers["content-type"].startswith("text/event-stream")
        assert response.headers["cache-control"] == "no-cache"
        assert response.headers["x-accel-buffering"] == "no"  # buffering is explicit: none, here or in a proxy
        events = parse_sse(response.text)
        result = final_of(events)
        assert stages(events) == [
            "triage",
            "routed",
            "specialist",
            "tool_call",
        ]
        assert [e for e in events if e["type"] == "turn.progress"][3]["data"]["tool"] == "get_order_status"
        deltas = [e for e in events if e["type"] == "reply.delta"]
        assert len(deltas) > 1  # the model's words as they arrived, not one buffered block
        assert not [e for e in events if e["type"] == "reply.reset"]
        assert result["outcome"] == "completed"
        assert streamed_text(events) == result["reply"]["text"]
        assert "ORD-1002" in result["reply"]["text"]
        # The final event is the stored turn: the status lookup returns the same result.
        stored = client.get(f"/api/v1/conversations/{conversation}/turns/{result['turn_id']}").json()
        assert {k: v for k, v in stored.items() if k != "trace_id"} == {
            k: v for k, v in result.items() if k != "trace_id"
        }


def says_then_asks(messages: Sequence[Message], options: Mapping[str, Any]) -> ChatResponse:
    """A model that announces the cancellation before asking for it (seen natively: text arrives before the request)."""
    if "cancel_order" in tool_names(options) and not any(
        c.type == "function_call" for m in messages for c in m.contents
    ):
        return ChatResponse(
            messages=[
                Message(
                    role="assistant",
                    contents=[
                        Content.from_text("Sure, I will cancel ORD-1003 now."),
                        Content.from_function_call(
                            call_id="c1", name="cancel_order", arguments={"order_id": "ORD-1003"}
                        ),
                    ],
                )
            ]
        )
    return default_script(messages, options)


def test_text_replaced_by_an_approval_request_is_reset_and_nothing_changes() -> None:
    app, _ = host()
    model(app).script = says_then_asks
    with TestClient(app) as client:
        conversation = Api(client).create_conversation()
        events = parse_sse(stream_turn(client, conversation, "Please cancel order ORD-1003.").text)

    result = final_of(events)
    types = [e["type"] for e in events]
    assert "reply.reset" in types
    assert types.index("reply.reset") > types.index("reply.delta")  # the model's words came first, then the reset
    assert "awaiting_approval" in stages(events)
    assert result["outcome"] == "awaiting_approval"
    assert streamed_text(events) == result["reply"]["text"]
    assert result["reply"]["text"].startswith("I can do this for you:")
    assert state_of(app, "cancel_order") == "processing"


def test_a_streamed_approval_reports_execution_and_the_committed_result() -> None:
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        asked = api.ask(conversation, "Please cancel order ORD-1003.")
        pending = asked["pending_approval"]
        response = client.post(
            f"/api/v1/conversations/{conversation}/approvals/{pending['approval_id']}",
            json={"operation_id": pending["operation_id"], "decision": "approve"},
            headers=SSE,
        )
        events = parse_sse(response.text)
        result = final_of(events)

        assert events[0]["data"] == {"client_turn_id": None, "resumed_from_turn_id": asked["turn_id"]}
        assert "executing_operation" in stages(events)
        assert result["outcome"] == "completed"
        assert result["reply"]["kind"] == "approval_result"
        assert streamed_text(events) == result["reply"]["text"]
        assert result["operations"] == [
            {"operation_id": pending["operation_id"], "kind": "cancel_order", "status": "committed"}
        ]
        assert state_of(app, "cancel_order") == "cancelled"

        # The same decision streamed again: the same turn, replayed, no second write.
        again = parse_sse(
            client.post(
                f"/api/v1/conversations/{conversation}/approvals/{pending['approval_id']}",
                json={"operation_id": pending["operation_id"], "decision": "approve"},
                headers=SSE,
            ).text
        )
        assert final_of(again)["turn_id"] == result["turn_id"]
        assert state_of(app, "cancel_order") == "cancelled"


def fails_after_speaking(messages: Sequence[Message], options: Mapping[str, Any]) -> ChatResponse:
    if any(c.type == "function_result" for c in messages[-1].contents):
        raise RuntimeError("Simulated provider failure after the tool call.")
    if "get_order_status" in tool_names(options):
        response = tool_call("get_order_status", {"order_id": "ORD-1002"})
        response.messages[0].contents.insert(0, Content.from_text("Let me check that order. "))
        return response
    return default_script(messages, options)


def test_a_failure_after_text_was_streamed_resets_it_and_ends_with_a_failed_result() -> None:
    app, _ = host()
    model(app).script = fails_after_speaking
    with TestClient(app) as client:
        conversation = Api(client).create_conversation()
        events = parse_sse(stream_turn(client, conversation, "Where is my order ORD-1002?").text)

    result = final_of(events)
    assert [e for e in events if e["type"] == "reply.delta"]  # text went out before the failure
    assert events[-2]["type"] == "reply.reset"  # and is withdrawn: a failed turn has no reply
    assert result["outcome"] == "failed"
    assert "reply" not in result
    assert streamed_text(events) == ""


def test_errors_before_the_stream_starts_are_ordinary_problem_responses() -> None:
    app, principal = host()
    with TestClient(app) as client:
        conversation = Api(client).create_conversation()
        key = str(uuid.uuid4())
        assert stream_turn(client, conversation, "Where is my order ORD-1002?", key).status_code == 200

        for response, status, code in [
            (stream_turn(client, conversation, "A different message.", key), 409, "client_turn_id_reused"),
            (stream_turn(client, "conv_0000000000000000", "Hi"), 404, "not_found"),
            (
                client.post(f"/api/v1/conversations/{conversation}/turns", json={"message": "Hi"}, headers=SSE),
                400,
                "invalid_request",
            ),
        ]:
            assert response.status_code == status
            assert response.headers["content-type"].startswith("application/problem+json")
            assert_contract("problem", response.json())
            assert response.json()["code"] == code

        principal.switch_to("CUST-1002")
        foreign = stream_turn(client, conversation, "Hi")
        assert (foreign.status_code, foreign.json()["code"]) == (404, "not_found")


def test_a_duplicate_streamed_submission_replays_the_same_turn_without_a_model_call() -> None:
    app, _ = host()
    with TestClient(app) as client:
        conversation = Api(client).create_conversation()
        key = str(uuid.uuid4())
        first = final_of(parse_sse(stream_turn(client, conversation, "Where is my order ORD-1002?", key).text))
        calls = model(app).calls

        events = parse_sse(stream_turn(client, conversation, "Where is my order ORD-1002?", key).text)

    result = final_of(events)
    assert result["turn_id"] == first["turn_id"]
    assert model(app).calls == calls
    assert stages(events) == []  # no live events: this request didn't run the turn
    assert streamed_text(events) == result["reply"]["text"]  # the text still follows the contract's rule


def test_a_long_reply_is_split_into_deltas_within_the_contract_limit() -> None:
    app, _ = host()
    long_reply = "x" * 4500  # one "word", so the model streams it as one piece and the server must split it

    def long(messages: Sequence[Message], options: Mapping[str, Any]) -> ChatResponse:
        if any(c.type == "function_result" for c in messages[-1].contents):
            return text(long_reply)
        return default_script(messages, options)

    model(app).script = long
    with TestClient(app) as client:
        conversation = Api(client).create_conversation()
        events = parse_sse(stream_turn(client, conversation, "Where is my order ORD-1002?").text)

    deltas = [e["data"]["text"] for e in events if e["type"] == "reply.delta"]
    assert [len(d) for d in deltas] == [2000, 2000, 500]
    assert streamed_text(events) == final_of(events)["reply"]["text"] == long_reply


def test_splitting_never_breaks_a_character() -> None:
    reply = "😀" * 2001 + "é"
    parts = streaming.pieces(reply)
    assert [len(p) for p in parts] == [2000, 2]
    assert "".join(parts) == reply


def test_a_quiet_stream_sends_keepalive_comments(monkeypatch: pytest.MonkeyPatch) -> None:
    import asyncio

    monkeypatch.setattr(streaming, "KEEPALIVE_SECONDS", 0.05)
    app, _ = host()

    async def slow() -> None:
        await asyncio.sleep(0.3)

    model(app).before_call = slow
    with TestClient(app) as client:
        conversation = Api(client).create_conversation()
        response = stream_turn(client, conversation, "Where is my order ORD-1002?")

    assert ": keepalive" in response.text
    assert final_of(parse_sse(response.text))["outcome"] == "completed"


def test_a_plain_json_request_still_gets_one_turn_result() -> None:
    app, _ = host()
    with TestClient(app) as client:
        conversation = Api(client).create_conversation()
        response = client.post(
            f"/api/v1/conversations/{conversation}/turns",
            json={"client_turn_id": str(uuid.uuid4()), "message": "Where is my order ORD-1002?"},
        )
    assert response.headers["content-type"].startswith("application/json")
    assert_contract("turn-result", response.json())
