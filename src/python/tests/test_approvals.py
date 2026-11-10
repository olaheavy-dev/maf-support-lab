"""L37: every simulated write waits for the customer's approval of the exact action, bound to the customer, the
conversation, the operation, and its arguments. No approval or a rejection changes nothing; an approved eligible write
changes simulated state once and reports the facts. Native approval and business authorization are separate checks.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from datetime import UTC, datetime
from typing import Any

import pytest
from agent_framework import ChatResponse, Message
from fastapi import FastAPI
from fastapi.testclient import TestClient

from support_agent.fake_model import ScriptedChatClient, default_script, last_user_text
from support_agent.identity import Principal, pinned
from support_agent.operations import Decision, Kind, WriteError, approval_scope
from tests.helpers import Api, assert_contract, execute, host, let_expire

WRITES = [
    ("Please cancel order ORD-1003.", "orders", "cancel_order", {"order_id": "ORD-1003"}),
    (
        "I want a refund for ORD-1001, the headphones crackle.",
        "billing",
        "refund_order",
        {"order_id": "ORD-1001", "reason": "I want a refund for ORD-1001, the headphones crackle."},
    ),
    (
        "Please open a support ticket: my fridge shows error Z-77.",
        "technical",
        "create_ticket",
        {
            "subject": "Support request",
            "description": "Please open a support ticket: my fridge shows error Z-77.",
            "priority": "normal",
        },
    ),
]


def state_of(app: FastAPI, kind: str) -> str:
    state = app.state.services.state
    if kind == "cancel_order":
        return str(state.orders["ORD-1003"].status)
    if kind == "refund_order":
        return f"refunds={len([p for p in state.payments if p.kind == 'refund' and p.customer_id == 'CUST-1001'])}"
    return f"tickets={len(state.tickets_of('CUST-1001'))}"


BEFORE = {"cancel_order": "processing", "refund_order": "refunds=0", "create_ticket": "tickets=0"}
AFTER = {"cancel_order": "cancelled", "refund_order": "refunds=1", "create_ticket": "tickets=1"}


def decide(
    client: TestClient, conversation: str, pending: dict[str, Any], decision: str, **extra: Any
) -> tuple[int, dict[str, Any]]:
    response = client.post(
        f"/api/v1/conversations/{conversation}/approvals/{pending['approval_id']}",
        json={"operation_id": pending["operation_id"], "decision": decision, **extra},
    )
    body: dict[str, Any] = response.json()
    assert_contract("turn-result" if response.status_code == 200 else "problem", body)
    return response.status_code, body


@pytest.mark.parametrize(("message", "target", "kind", "arguments"), WRITES, ids=[w[2] for w in WRITES])
def test_a_write_waits_for_approval_of_the_exact_action_and_runs_once_approved(
    message: str, target: str, kind: str, arguments: dict[str, str]
) -> None:
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        asked = api.ask(conversation, message)
        assert asked["outcome"] == "awaiting_approval"
        assert asked["route"]["target"] == target
        assert asked["reply"]["kind"] == "approval_request"
        pending = asked["pending_approval"]
        assert pending["kind"] == kind
        assert pending["arguments"] == arguments
        assert client.get(f"/api/v1/operations/{pending['operation_id']}").json()["status"] == "awaiting_approval"
        assert state_of(app, kind) == BEFORE[kind]  # nothing changed yet
        assert [
            p["operation_id"]
            for p in api.client.get(f"/api/v1/conversations/{conversation}").json()["pending_approvals"]
        ] == [pending["operation_id"]]

        status, decided = decide(client, conversation, pending, "approve")
        assert status == 200
        assert decided["reply"]["kind"] == "approval_result"
        # The model's own report of the committed write ("Done: ..."), not the factual fallback ("Done. ...") used when
        # the resumed model call fails: a resume that the provider would reject must not pass silently (found live).
        assert decided["reply"]["text"].startswith("Done:")
        assert decided["resumed_from_turn_id"] == asked["turn_id"]
        assert decided["client_turn_id"] is None
        operation = client.get(f"/api/v1/operations/{pending['operation_id']}").json()
        assert_contract("operation", operation)
        assert operation["status"] == "committed"
        assert state_of(app, kind) == AFTER[kind]
        assert client.get(f"/api/v1/conversations/{conversation}").json()["pending_approvals"] == []


@pytest.mark.parametrize(("message", "target", "kind", "arguments"), WRITES, ids=[w[2] for w in WRITES])
def test_a_rejected_write_changes_nothing_and_a_stubborn_model_is_not_obeyed(
    message: str, target: str, kind: str, arguments: dict[str, str]
) -> None:
    _ = (target, arguments)
    app, _ = host()
    model: ScriptedChatClient = app.state.agents.triage.client
    calls = {"n": 0}

    def stubborn(messages: Sequence[Message], options: Mapping[str, Any]) -> ChatResponse:
        # Requests the same write every time, even after the rejection (which tells it not to retry).
        calls["n"] += 1
        return default_script(
            [Message(role="user", contents=[last_user_text([m for m in messages if m.role == "user" and m.text][:1])])],
            options,
        )

    model.script = stubborn
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        pending = api.ask(conversation, message)["pending_approval"]
        status, decided = decide(client, conversation, pending, "reject", reason="I changed my mind.")
        assert status == 200
        assert decided["reply"]["kind"] == "approval_result"
        assert "Nothing has been changed" in decided["reply"]["text"]
        assert client.get(f"/api/v1/operations/{pending['operation_id']}").json()["status"] == "rejected"
        assert state_of(app, kind) == BEFORE[kind]
        assert len(app.state.operations.store.for_conversation(conversation)) == 1  # never re-proposed
        assert calls["n"] <= 1 + 1 + 3  # ask, resume after rejection, then at most 3 bounded declines


@pytest.mark.parametrize(
    ("principal", "message", "kind"),
    [
        ("CUST-1001", "Cancel order ORD-1001.", "not_eligible"),  # already delivered
        ("CUST-1002", "Refund my keyboard order ORD-2001 please.", "not_eligible"),  # outside the 30-day window
        ("CUST-1001", "Refund ORD-2003 to my card right away.", "not_found"),  # another customer's order
        ("CUST-1001", "Please cancel order ORD-9999.", "not_found"),
    ],
)
def test_an_ineligible_or_foreign_write_is_never_proposed(principal: str, message: str, kind: str) -> None:
    app, _ = host(principal)
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        turn = api.ask(conversation, message)
        assert turn["outcome"] == "completed"
        assert turn["reply"]["kind"] == kind
        assert turn["operations"] == []
        assert app.state.operations.store.for_conversation(conversation) == []


def test_an_approval_is_bound_to_its_customer_conversation_operation_and_decision() -> None:
    app, principal = host("CUST-1001")
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        other_conversation = api.create_conversation()
        pending = api.ask(conversation, "Please cancel order ORD-1003.")["pending_approval"]

        principal.switch_to("CUST-1002")
        assert decide(client, conversation, pending, "approve")[0] == 404  # another customer
        principal.switch_to("CUST-1001")
        assert decide(client, other_conversation, pending, "approve")[0] == 404  # another conversation
        status, mismatch = decide(client, conversation, {**pending, "operation_id": "op_0000000000000000"}, "approve")
        assert (status, mismatch["code"]) == (409, "approval_mismatch")
        status, invalid = decide(client, conversation, pending, "approve", arguments={"order_id": "ORD-1002"})
        assert (status, invalid["code"]) == (400, "invalid_request")  # the decision cannot carry arguments
        assert app.state.services.state.orders["ORD-1003"].status == "processing"

        first = decide(client, conversation, pending, "approve")[1]
        again = decide(client, conversation, pending, "approve")[1]
        assert again["turn_id"] == first["turn_id"]  # repeating the decision replays; the write ran once
        status, conflict = decide(client, conversation, pending, "reject")
        assert (status, conflict["code"]) == (409, "approval_already_decided")
        assert app.state.services.state.orders["ORD-1003"].status == "cancelled"


def test_an_expired_approval_cannot_run() -> None:
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        pending = api.ask(conversation, "Please cancel order ORD-1003.")["pending_approval"]
        let_expire(app, pending["operation_id"])
        status, problem = decide(client, conversation, pending, "approve")
        assert (status, problem["code"]) == (410, "approval_expired")
        assert client.get(f"/api/v1/operations/{pending['operation_id']}").json()["status"] == "expired"
        assert app.state.services.state.orders["ORD-1003"].status == "processing"


def test_eligibility_is_checked_again_when_the_approved_write_runs() -> None:
    # Two approvals for the same order: after the first commits, the second is no longer eligible.
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        first, second = api.create_conversation(), api.create_conversation()
        a = api.ask(first, "Please cancel order ORD-1003.")["pending_approval"]
        b = api.ask(second, "Please cancel order ORD-1003.")["pending_approval"]
        decide(client, first, a, "approve")
        status, late = decide(client, second, b, "approve")
        assert status == 200
        assert "Nothing has been changed" in late["reply"]["text"]
        operation = client.get(f"/api/v1/operations/{b['operation_id']}").json()
        assert operation["status"] == "failed"
        assert operation["failure"]["code"] == "not_eligible"


def test_a_write_cannot_run_without_an_approval_for_exactly_its_arguments() -> None:
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        pending = api.ask(conversation, "Please cancel order ORD-1003.")["pending_approval"]
    operations = app.state.operations
    operation = operations.store.find(pending["operation_id"])
    with pinned(Principal("CUST-1001")):
        with pytest.raises(WriteError, match="not been approved"):
            execute(operations, Principal("CUST-1001"), Kind.CANCEL_ORDER, {"order_id": "ORD-1003"})  # no scope
        assert operation.decide(Decision.APPROVE, datetime.now(UTC))
        with approval_scope(operation):
            for kind, arguments in [
                (Kind.CANCEL_ORDER, {"order_id": "ORD-1002"}),
                (Kind.REFUND_ORDER, {"order_id": "ORD-1003", "reason": "x"}),
            ]:
                with pytest.raises(WriteError, match="not been approved"):
                    execute(operations, Principal("CUST-1001"), kind, arguments)  # changed arguments or kind
            with pytest.raises(WriteError, match="not been approved"):
                execute(
                    operations, Principal("CUST-1002"), Kind.CANCEL_ORDER, {"order_id": "ORD-1003"}
                )  # another customer
    assert app.state.services.state.orders["ORD-1003"].status == "processing"
    assert app.state.services.state.orders["ORD-1002"].status == "shipped"


def test_the_longest_allowed_reason_still_gives_a_contract_valid_summary() -> None:
    app, _ = host()
    message = "I want a refund for ORD-1001. " + "x" * (500 - len("I want a refund for ORD-1001. "))
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        pending = api.ask(conversation, message)["pending_approval"]  # validated against the contract by ask()
        assert len(pending["arguments"]["reason"]) == 500
        assert len(pending["summary"]) <= 500
        assert_contract("conversation", client.get(f"/api/v1/conversations/{conversation}").json())


@pytest.mark.parametrize("decision", ["approve", "reject"])
def test_a_decision_stays_replayable_when_the_resumed_model_call_fails(decision: str) -> None:
    app, _ = host()
    model: ScriptedChatClient = app.state.agents.triage.client

    def fails_after_tool(messages: Sequence[Message], options: Mapping[str, Any]) -> ChatResponse:
        if any(content.type == "function_result" for content in messages[-1].contents) or any(
            content.type == "function_approval_response" for m in messages for content in m.contents
        ):
            raise RuntimeError("Simulated provider failure after the decision.")
        return default_script(messages, options)

    model.script = fails_after_tool
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        pending = api.ask(conversation, "Please cancel order ORD-1003.")["pending_approval"]
        status, decided = decide(client, conversation, pending, decision)
        again_status, again = decide(client, conversation, pending, decision)
        assert (status, again_status) == (200, 200)
        assert again["turn_id"] == decided["turn_id"]
        assert decided["reply"]["kind"] == "approval_result"
        expected = {"approve": ("committed", "cancelled"), "reject": ("rejected", "processing")}[decision]
        assert client.get(f"/api/v1/operations/{pending['operation_id']}").json()["status"] == expected[0]
        assert app.state.services.state.orders["ORD-1003"].status == expected[1]
