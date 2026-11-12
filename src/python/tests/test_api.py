"""The version-1 contract API in Python: validation, ownership, idempotency, and controlled failures."""

from __future__ import annotations

import json
import uuid

import pytest
from fastapi.testclient import TestClient

from support_agent.fake_model import (
    FAILURE_TRIGGER,
    MALFORMED_TRIAGE_TRIGGER,
    UNKNOWN_ROUTE_TRIGGER,
    ScriptedChatClient,
)
from tests.helpers import Api, assert_contract, host


@pytest.mark.parametrize(
    ("body", "pointer"),
    [
        ({"client_turn_id": str(uuid.uuid4()), "message": "Hi", "customer_id": "CUST-1002"}, ""),
        ({"client_turn_id": "not-a-uuid", "message": "Hi"}, "/client_turn_id"),
        ({"client_turn_id": str(uuid.uuid4()), "message": ""}, "/message"),
        ({"message": "Hi"}, ""),  # the whole body (RFC 6901), which lacks client_turn_id
    ],
)
def test_requests_that_break_the_contract_get_invalid_request(body: dict[str, object], pointer: str) -> None:
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        response = client.post(f"/api/v1/conversations/{conversation}/turns", json=body)
        assert response.status_code == 400
        assert response.headers["content-type"].startswith("application/problem+json")
        assert_contract("problem", response.json())
        assert response.json()["code"] == "invalid_request"
        assert pointer in [error["pointer"] for error in response.json()["errors"]]


def test_another_customers_conversation_and_turns_look_exactly_like_missing_ones() -> None:
    app, principal = host("CUST-1001")
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        turn = api.ask(conversation, "Where is my order ORD-1002?")
        principal.switch_to("CUST-1002")
        responses = {
            "conversation": (
                client.get(f"/api/v1/conversations/{conversation}"),
                client.get("/api/v1/conversations/conv_0000000000000000"),
            ),
            "turn": (
                client.get(f"/api/v1/conversations/{conversation}/turns/{turn['turn_id']}"),
                client.get(f"/api/v1/conversations/conv_0000000000000000/turns/{turn['turn_id']}"),
            ),
            "send": (
                client.post(
                    f"/api/v1/conversations/{conversation}/turns",
                    json={"client_turn_id": str(uuid.uuid4()), "message": "Hi"},
                ),
                client.post(
                    "/api/v1/conversations/conv_0000000000000000/turns",
                    json={"client_turn_id": str(uuid.uuid4()), "message": "Hi"},
                ),
            ),
        }
        for name, (denied, missing) in responses.items():
            assert denied.status_code == 404, name
            assert without_trace(denied.json()) == without_trace(missing.json()), name


def without_trace(body: dict[str, object]) -> dict[str, object]:
    return {key: value for key, value in body.items() if key != "trace_id"}


def test_a_repeated_client_turn_id_returns_the_original_turn_without_running_it_again() -> None:
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        key = str(uuid.uuid4())
        first = api.ask(conversation, "Where is my order ORD-1002?", key)
        model: ScriptedChatClient = app.state.agents.triage.client
        calls = model.calls
        again = api.ask(conversation, "Where is my order ORD-1002?", key)
        assert again == first
        assert model.calls == calls
        status, conflict = api.send(conversation, "Something else", key)
        assert status == 409
        assert conflict["code"] == "client_turn_id_reused"


def test_a_turn_that_fails_is_a_failed_result_not_an_http_error() -> None:
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        turn = api.ask(api.create_conversation(), f"Where is ORD-1002? {FAILURE_TRIGGER}")
        assert turn["outcome"] == "failed"
        assert turn["error"]["code"] == "internal_error"
        assert "reply" not in turn
        assert FAILURE_TRIGGER not in str(turn)  # nothing from the exception leaks into the result


@pytest.mark.parametrize("marker", [MALFORMED_TRIAGE_TRIGGER, UNKNOWN_ROUTE_TRIGGER])
def test_invalid_triage_output_reaches_the_controlled_fallback(marker: str) -> None:
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        turn = api.ask(api.create_conversation(), f"Where is ORD-1002? {marker}")
        assert turn["route"] == {"status": "unrouted", "target": None, "unrouted_reason": "invalid_model_output"}
        assert turn["reply"]["kind"] == "clarification"


def test_a_write_request_only_asks_for_approval_and_changes_nothing() -> None:
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        turn = api.ask(api.create_conversation(), "Please cancel order ORD-1003.")
        assert turn["outcome"] == "awaiting_approval"  # a pause, not success
        assert turn["reply"]["kind"] == "approval_request"
        assert app.state.services.state.orders["ORD-1003"].status == "processing"


@pytest.mark.parametrize(
    ("target", "action"),
    [("billing", "order_status"), ("technical", "invoice"), ("orders", "troubleshooting"), ("orders", "other")],
)
def test_a_triage_decision_whose_action_and_target_disagree_is_never_routed(target: str, action: str) -> None:
    # The stretch: a well-formed decision that is still ambiguous. Code, not the model, decides the route.
    app, _ = host()
    decision = {
        "target": target,
        "requested_action": action,
        "order_id": "ORD-1002",
        "summary": "x",
        "customer_sentiment": "neutral",
        "confidence": 0.99,
    }
    model: ScriptedChatClient = app.state.agents.triage.client
    model.triage = lambda _: json.dumps(decision)
    with TestClient(app) as client:
        api = Api(client)
        turn = api.ask(api.create_conversation(), "Where is my order ORD-1002?")
        assert turn["route"] == {"status": "unrouted", "target": None, "unrouted_reason": "ambiguous_request"}
        assert turn["reply"]["kind"] == "clarification"


def test_a_turn_that_fails_after_triage_still_reports_its_route() -> None:
    """Found by the cross-backend probes: like .NET, a failed turn keeps the route triage chose."""
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        result = api.ask(api.create_conversation(), "Where is ORD-1002? [fake:model-error]")
    assert result["outcome"] == "failed"
    assert result["error"]["code"] == "internal_error"
    assert result["route"] == {"status": "routed", "target": "orders"}
