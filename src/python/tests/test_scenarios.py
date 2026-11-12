"""The shared feature scenarios (fixtures/scenarios/feature-scenarios.json), all of them.

The same file drives the .NET backend, so both implementations are held to one description of behaviour. Steps send
a message, resend the previous one, or decide the pending approval (optionally as another customer); every response
is checked against the contract, and each scenario's expected state change is checked at the end.
"""

from __future__ import annotations

import json
import uuid
from typing import Any

import pytest
from fastapi.testclient import TestClient

from support_agent.fixtures import REPOSITORY_ROOT
from tests.helpers import Api, assert_contract, host

SCENARIOS: list[dict[str, Any]] = json.loads(
    (REPOSITORY_ROOT / "fixtures" / "scenarios" / "feature-scenarios.json").read_text(encoding="utf-8")
)["scenarios"]


def test_every_scenario_in_the_file_runs() -> None:
    assert len(SCENARIOS) == 19


@pytest.mark.parametrize("scenario", SCENARIOS, ids=[s["scenario_id"] for s in SCENARIOS])
def test_scenario_behaves_as_described(scenario: dict[str, Any]) -> None:
    owner: str = scenario["principal"]
    app, principal = host(owner)
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        pending: dict[str, Any] | None = None
        last_turn: str | None = None
        last_operation: str | None = None
        last_request: dict[str, str] | None = None

        for step in scenario["steps"]:
            expect: dict[str, Any] = step["expect"]
            principal.switch_to(step.get("as_principal", owner))
            if step["action"] == "decide":
                assert pending is not None, "no pending approval to decide"
                response = client.post(
                    f"/api/v1/conversations/{conversation}/approvals/{pending['approval_id']}",
                    json={"operation_id": pending["operation_id"], "decision": step["decision"]},
                )
            else:
                if step["action"] != "resend_previous":
                    last_request = {"client_turn_id": str(uuid.uuid4()), "message": step["message"]}
                response = client.post(f"/api/v1/conversations/{conversation}/turns", json=last_request)
            principal.switch_to(owner)
            body: dict[str, Any] = response.json()

            if "http_status" in expect:
                assert response.status_code == expect["http_status"]
                assert_contract("problem", body)
                assert body["code"] == expect["problem_code"]
                continue

            assert response.status_code == 200, body
            assert_contract("turn-result", body)
            route = body.get("route", {})
            if "route_status" in expect:
                assert route["status"] == expect["route_status"]
            if "route_target" in expect:
                assert route.get("target") == expect["route_target"]
            if "outcome" in expect:
                assert body["outcome"] == expect["outcome"]
            if "reply_kind" in expect:
                assert body["reply"]["kind"] == expect["reply_kind"]
            for mention in expect.get("mentions", []):
                assert mention.lower() in body["reply"]["text"].lower()
            for forbidden in expect.get("must_not_mention", []):
                assert forbidden.lower() not in body["reply"]["text"].lower()
            if expect.get("same_turn_as_previous"):
                assert body["turn_id"] == last_turn

            operation_id = (
                body["pending_approval"]["operation_id"]
                if "pending_approval" in body
                else next((op["operation_id"] for op in body["operations"]), None)
            )
            if "operation_kind" in expect:
                assert body["pending_approval"]["kind"] == expect["operation_kind"]
            if "operation_status" in expect:
                operation = client.get(f"/api/v1/operations/{operation_id}").json()
                assert_contract("operation", operation)
                assert operation["status"] == expect["operation_status"]
            if expect.get("same_operation_as_previous"):
                assert operation_id == last_operation

            pending = body.get("pending_approval", pending)
            last_turn = body["turn_id"]
            last_operation = operation_id or last_operation

        state = app.state.services.state
        committed = [op for op in app.state.operations.store.for_conversation(conversation) if op.status == "committed"]
        change = scenario["expected_state_change"]
        match change["type"]:
            case "none":
                assert committed == []
            case "order_cancelled":
                assert state.orders[change["order_id"]].status == "cancelled"
                assert len(committed) == 1
            case "refund_recorded":
                refunds = [
                    p
                    for p in state.payments
                    if p.kind == "refund" and p.order_id == change["order_id"] and p.payment_id.startswith("PAY-8")
                ]
                assert len(refunds) == 1
                assert state.orders[change["order_id"]].refund_status == "refunded"
            case "ticket_created":
                assert len(state.tickets_of(owner)) == 1
            case other:
                pytest.fail(f"Unknown expected_state_change type: {other}")
