"""Cooperative cancellation (porting .NET the CancellationTests).

``POST …/turns/{id}/cancel`` asks a running turn to stop. It stops at its next await, or at the pre-commit boundary
before any write. Cancelled before a write commits: the turn is ``cancelled``, the approved operation ``failed`` with
code ``cancelled``, and nothing changed. Cancelled after the commit: the committed result stands, and the turn reports
it. Nothing is ever rolled back.
"""

from __future__ import annotations

import asyncio
import uuid
from datetime import UTC, datetime
from typing import Any

import httpx
import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from support_agent.api import complete_decision, complete_turn
from support_agent.conversations import Claim
from support_agent.fake_model import ScriptedChatClient
from support_agent.identity import Principal, pinned
from support_agent.operations import Decision, Kind, Status, approval_scope
from support_agent.turn_control import TurnCancelled, TurnControl, controlling
from tests.helpers import assert_contract, host
from tests.test_approvals import AFTER, BEFORE, state_of

TICKET = "Please open a support ticket: my fridge shows error Z-77."


@pytest.fixture
def anyio_backend() -> str:
    return "asyncio"


def client_for(app: FastAPI) -> httpx.AsyncClient:
    return httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test")


def model(app: FastAPI) -> ScriptedChatClient:
    return app.state.agents.triage.client


class Gate:
    """Holds a turn at a chosen point until the test releases it."""

    def __init__(self) -> None:
        self.entered, self.release = asyncio.Event(), asyncio.Event()

    async def hold(self) -> None:
        self.entered.set()
        await self.release.wait()

    async def reached(self) -> None:
        await asyncio.wait_for(self.entered.wait(), 5)


async def new_conversation(client: httpx.AsyncClient) -> str:
    return str((await client.post("/api/v1/conversations", json={})).json()["conversation_id"])


def running_turn_id(app: FastAPI, conversation: str) -> str:
    store = app.state.conversations
    found = store._conversations[conversation]
    assert found.active_turn_id is not None
    return str(found.active_turn_id)


async def cancel(client: httpx.AsyncClient, conversation: str, turn_id: str) -> httpx.Response:
    return await client.post(f"/api/v1/conversations/{conversation}/turns/{turn_id}/cancel")


@pytest.mark.anyio
async def test_a_running_turn_can_be_cancelled_once_and_the_conversation_moves_on() -> None:
    app, principal = host()
    gate = Gate()
    async with client_for(app) as client:
        conversation = await new_conversation(client)
        model(app).before_call = gate.hold
        asking = asyncio.create_task(
            client.post(
                f"/api/v1/conversations/{conversation}/turns",
                json={"client_turn_id": str(uuid.uuid4()), "message": "Where is my order ORD-1002?"},
            )
        )
        await gate.reached()
        turn_id = running_turn_id(app, conversation)

        running = (await client.get(f"/api/v1/conversations/{conversation}/turns/{turn_id}")).json()
        assert running["state"] == "running"

        principal.switch_to("CUST-1002")
        assert (await cancel(client, conversation, turn_id)).status_code == 404  # never another customer's turn
        principal.switch_to("CUST-1001")

        accepted = await cancel(client, conversation, turn_id)
        assert accepted.status_code == 202
        assert_contract("cancel-turn-response", accepted.json())
        assert accepted.json() == {"contract_version": "1.1", "turn_id": turn_id, "cancel_requested": True}

        turn = (await asyncio.wait_for(asking, 5)).json()  # the held model call was interrupted: no release needed
        assert_contract("turn-result", turn)
        assert (turn["state"], turn["outcome"]) == ("finished", "cancelled")
        assert "reply" not in turn
        assert (await client.get(f"/api/v1/conversations/{conversation}/turns/{turn_id}")).json() == turn

        again = await cancel(client, conversation, turn_id)
        assert (again.status_code, again.json()["code"]) == (409, "turn_already_finished")
        assert_contract("problem", again.json())
        unknown = await cancel(client, conversation, "turn_0000000000000000")
        assert unknown.status_code == 404

        model(app).before_call = None
        next_turn = await client.post(
            f"/api/v1/conversations/{conversation}/turns",
            json={"client_turn_id": str(uuid.uuid4()), "message": "Where is my order ORD-1002?"},
        )
        assert next_turn.json()["outcome"] == "completed"  # the conversation isn't stuck


async def ask_for_approval(client: httpx.AsyncClient, conversation: str, message: str) -> dict[str, Any]:
    response = await client.post(
        f"/api/v1/conversations/{conversation}/turns", json={"client_turn_id": str(uuid.uuid4()), "message": message}
    )
    pending: dict[str, Any] = response.json()["pending_approval"]
    return pending


def decide(client: httpx.AsyncClient, conversation: str, pending: dict[str, Any]) -> asyncio.Task[httpx.Response]:
    return asyncio.create_task(
        client.post(
            f"/api/v1/conversations/{conversation}/approvals/{pending['approval_id']}",
            json={"operation_id": pending["operation_id"], "decision": "approve"},
        )
    )


@pytest.mark.anyio
async def test_cancelled_before_the_commit_nothing_changes_and_the_operation_fails_as_cancelled() -> None:
    app, _ = host()
    gate = Gate()
    app.state.operations.before_commit = gate.hold  # the approved write is about to start
    async with client_for(app) as client:
        conversation = await new_conversation(client)
        pending = await ask_for_approval(client, conversation, TICKET)
        deciding = decide(client, conversation, pending)
        await gate.reached()

        assert (await cancel(client, conversation, running_turn_id(app, conversation))).status_code == 202
        turn = (await asyncio.wait_for(deciding, 5)).json()

        assert_contract("turn-result", turn)
        assert turn["outcome"] == "cancelled"
        assert turn["resumed_from_turn_id"]
        operation = (await client.get(f"/api/v1/operations/{pending['operation_id']}")).json()
        assert_contract("operation", operation)
        assert (operation["status"], operation["failure"]["code"]) == ("failed", "cancelled")
        assert state_of(app, "create_ticket") == BEFORE["create_ticket"]

        # The same decision again replays the cancelled turn; it never writes later.
        app.state.operations.before_commit = None
        replay = await decide(client, conversation, pending)
        assert replay.json()["turn_id"] == turn["turn_id"]
        assert state_of(app, "create_ticket") == BEFORE["create_ticket"]


def test_the_pre_commit_boundary_itself_refuses_to_write_once_cancellation_was_requested() -> None:
    # Without any task to interrupt: the flag alone stops the write. The operation stays running (nothing happened).
    app, _ = host()
    with TestClient(app) as client:
        conversation = client.post("/api/v1/conversations", json={}).json()["conversation_id"]
        pending = client.post(
            f"/api/v1/conversations/{conversation}/turns",
            json={"client_turn_id": str(uuid.uuid4()), "message": TICKET},
        ).json()["pending_approval"]
    store = app.state.operations.store
    operation = store.find(pending["operation_id"])
    assert store.decide(operation, Decision.APPROVE, None, datetime.now(UTC))
    control = TurnControl()
    control.request_cancel()

    async def attempt() -> None:
        with controlling(control), pinned(Principal("CUST-1001")), approval_scope(operation):
            await app.state.operations.execute_approved(Principal("CUST-1001"), Kind.CREATE_TICKET, operation.arguments)

    with pytest.raises(TurnCancelled):
        asyncio.run(attempt())
    assert state_of(app, "create_ticket") == BEFORE["create_ticket"]
    assert store.find(pending["operation_id"]).status is Status.RUNNING


@pytest.mark.anyio
async def test_cancelled_after_the_commit_the_committed_result_stands() -> None:
    app, _ = host()
    gate = Gate()
    async with client_for(app) as client:
        conversation = await new_conversation(client)
        pending = await ask_for_approval(client, conversation, "Please cancel order ORD-1003.")

        async def hold_after_commit() -> None:
            stored = app.state.operations.store.find(pending["operation_id"])
            if stored is not None and stored.status is Status.COMMITTED:
                await gate.hold()

        model(app).before_call = hold_after_commit  # the model call that follows the committed write
        deciding = decide(client, conversation, pending)
        await gate.reached()
        assert (await cancel(client, conversation, running_turn_id(app, conversation))).status_code == 202
        turn = (await asyncio.wait_for(deciding, 5)).json()

        assert_contract("turn-result", turn)
        assert (turn["outcome"], turn["reply"]["kind"]) == ("completed", "approval_result")
        assert turn["reply"]["text"].startswith("Done.")  # from the record: the model call was interrupted
        assert turn["operations"][0]["status"] == "committed"
        assert (await client.get(f"/api/v1/operations/{pending['operation_id']}")).json()["status"] == "committed"
        assert state_of(app, "cancel_order") == AFTER["cancel_order"]


@pytest.mark.anyio
async def test_a_rejection_cancelled_mid_run_stays_rejected_and_changes_nothing() -> None:
    app, _ = host()
    gate = Gate()
    async with client_for(app) as client:
        conversation = await new_conversation(client)
        pending = await ask_for_approval(client, conversation, "Please cancel order ORD-1003.")
        model(app).before_call = gate.hold  # the model call that follows the native rejection
        rejecting = asyncio.create_task(
            client.post(
                f"/api/v1/conversations/{conversation}/approvals/{pending['approval_id']}",
                json={"operation_id": pending["operation_id"], "decision": "reject"},
            )
        )
        await gate.reached()
        await cancel(client, conversation, running_turn_id(app, conversation))
        turn = (await asyncio.wait_for(rejecting, 5)).json()

    assert turn["outcome"] == "cancelled"
    assert app.state.operations.store.find(pending["operation_id"]).status is Status.REJECTED
    assert state_of(app, "cancel_order") == "processing"


@pytest.mark.anyio
async def test_a_dropped_json_request_does_not_stop_the_turn() -> None:
    app, _ = host()
    gate = Gate()
    async with client_for(app) as client:
        conversation = await new_conversation(client)
        model(app).before_call = gate.hold
        asking = asyncio.create_task(
            client.post(
                f"/api/v1/conversations/{conversation}/turns",
                json={"client_turn_id": str(uuid.uuid4()), "message": "Where is my order ORD-1002?"},
            )
        )
        await gate.reached()
        turn_id = running_turn_id(app, conversation)
        asking.cancel()  # the client gives up: not a cancellation request
        model(app).before_call = None
        gate.release.set()

        for _ in range(100):
            if turn_id in app.state.conversations._conversations[conversation].turns:
                break
            await asyncio.sleep(0.02)
        turn = (await client.get(f"/api/v1/conversations/{conversation}/turns/{turn_id}")).json()
    assert (turn["state"], turn["outcome"]) == ("finished", "completed")


@pytest.mark.anyio
async def test_shutdown_cancels_running_turns_and_stores_them_before_closing_the_database() -> None:
    app, _ = host()
    gate = Gate()
    async with app.router.lifespan_context(app), client_for(app) as client:
        conversation = await new_conversation(client)
        model(app).before_call = gate.hold
        asking = asyncio.create_task(
            client.post(
                f"/api/v1/conversations/{conversation}/turns",
                json={"client_turn_id": str(uuid.uuid4()), "message": "Where is my order ORD-1002?"},
            )
        )
        await gate.reached()
        turn_id = running_turn_id(app, conversation)
    # The lifespan ended: the held turn was cancelled cooperatively and stored, then the database was closed.
    turn = (await asyncio.wait_for(asking, 5)).json()
    assert turn["outcome"] == "cancelled"
    assert len(app.state.turns) == 0
    assert "database" in app.state.resources.closed
    stored = app.state.database.query("SELECT COUNT(*) FROM turns WHERE turn_id = ?", (turn_id,))
    assert stored[0][0] == 1


def test_a_claim_has_its_own_control() -> None:
    first = Claim("turn_a", "x", asyncio.Future(loop=asyncio.new_event_loop()))
    second = Claim("turn_b", "x", asyncio.Future(loop=asyncio.new_event_loop()))
    assert first.control is not second.control


@pytest.mark.anyio
async def test_a_turn_cancelled_before_its_task_first_runs_is_still_stored_as_cancelled() -> None:
    # Codex review on PR #92: shutdown right after detach(), before the task ever ran. The task must still reach its
    # handler, store a cancelled turn, and answer its waiters.
    app, _ = host()
    async with client_for(app) as client:
        conversation_id = await new_conversation(client)
    conversation = app.state.conversations._conversations[conversation_id]
    claim, _ = conversation.reserve(str(uuid.uuid4()), "Where is my order ORD-1002?")
    assert claim is not None
    work = complete_turn(
        app.state.agents, app.state.operations, conversation, claim, claim.message, Principal("CUST-1001")
    )
    app.state.turns.detach(work, claim.control)
    await app.state.turns.shutdown()  # no await in between: the task has not started

    turn = await asyncio.wait_for(asyncio.shield(claim.done), 5)
    assert turn.outcome == "cancelled"
    assert claim.turn_id in conversation.turns
    assert conversation.active_turn_id is None
    assert model(app).calls == 0  # nothing ran


@pytest.mark.anyio
async def test_an_approval_cancelled_before_its_task_first_runs_fails_as_cancelled_and_changes_nothing() -> None:
    app, _ = host()
    async with client_for(app) as client:
        conversation_id = await new_conversation(client)
        pending = await ask_for_approval(client, conversation_id, TICKET)
    conversation = app.state.conversations._conversations[conversation_id]
    store = app.state.operations.store
    operation = store.find(pending["operation_id"])
    assert store.decide(operation, Decision.APPROVE, None, datetime.now(UTC))
    claim, _ = conversation.reserve(f"approval:{operation.approval_id}", "approve")
    assert claim is not None
    work = complete_decision(
        app.state.agents, app.state.operations, conversation, claim, operation, Principal("CUST-1001")
    )
    app.state.turns.detach(work, claim.control)
    await app.state.turns.shutdown()

    turn = await asyncio.wait_for(asyncio.shield(claim.done), 5)
    assert turn.outcome == "cancelled"
    stored = store.find(pending["operation_id"])
    assert (stored.status, stored.failure[0]) == (Status.FAILED, "cancelled")
    assert state_of(app, "create_ticket") == BEFORE["create_ticket"]
