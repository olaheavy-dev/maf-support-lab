"""L38: state survives a restart, and every write applies at most once (porting .NET L11's DurableStateTests).

A "restart" is a new host on the same SQLite file after the first host stopped (or, for the crash cases, while the
first host is stuck mid-turn). Simulated failures use SQLite triggers, so the real code path fails at the real write.
"""

from __future__ import annotations

import asyncio
import sqlite3
import uuid
from collections.abc import Mapping, Sequence
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import httpx
import pytest
from agent_framework import ChatResponse, Message
from fastapi import FastAPI
from fastapi.testclient import TestClient

from support_agent.database import Database
from support_agent.fake_model import ScriptedChatClient, default_script
from support_agent.identity import Principal, pinned
from support_agent.operations import Decision, Kind, Operation, Status, approval_scope
from tests.helpers import Api, assert_contract, host, let_expire, new_database_path
from tests.test_approvals import AFTER, BEFORE, WRITES, decide, state_of

CANCEL = "Please cancel order ORD-1003."
TICKET = "Please open a support ticket: my fridge shows error Z-77."
ALL_WRITES = [(message, kind) for message, _, kind, _ in WRITES]


@pytest.fixture
def anyio_backend() -> str:
    return "asyncio"


def database_of(app: FastAPI) -> Database:
    return app.state.database


def model_of(app: FastAPI) -> ScriptedChatClient:
    return app.state.agents.triage.client


def ask_in_one_host(storage: Path, message: str) -> tuple[str, dict[str, Any], str]:
    """Asks for a write in a host that then stops. Returns the conversation, the pending approval, and the turn id."""
    app, _ = host(storage=storage)
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        asked = api.ask(conversation, message)
        return conversation, asked["pending_approval"], asked["turn_id"]


def operation_status(client: TestClient, operation_id: str) -> str:
    return str(client.get(f"/api/v1/operations/{operation_id}").json()["status"])


@pytest.mark.parametrize(("message", "kind"), ALL_WRITES)
def test_a_pending_approval_survives_a_restart_and_runs_once_when_approved(message: str, kind: str) -> None:
    storage = new_database_path()
    conversation, pending, asked_turn_id = ask_in_one_host(storage, message)

    after, _ = host(storage=storage)
    with TestClient(after) as client:
        restored = client.get(f"/api/v1/conversations/{conversation}").json()
        assert [p["operation_id"] for p in restored["pending_approvals"]] == [pending["operation_id"]]
        assert state_of(after, kind) == BEFORE[kind]

        status, decided = decide(client, conversation, pending, "approve")

        assert status == 200
        assert decided["reply"]["kind"] == "approval_result"
        assert decided["resumed_from_turn_id"] == asked_turn_id
        assert operation_status(client, pending["operation_id"]) == "committed"
        assert state_of(after, kind) == AFTER[kind]

    # Restarted again: repeating the decision replays the stored turn and never writes again.
    later, _ = host(storage=storage)
    with TestClient(later) as client:
        calls = model_of(later).calls
        status, replayed = decide(client, conversation, pending, "approve")
        assert (status, replayed["turn_id"]) == (200, decided["turn_id"])
        assert state_of(later, kind) == AFTER[kind]
        assert model_of(later).calls == calls  # replayed from storage, no model call


def test_after_a_restart_a_wrong_owner_mismatch_or_rejection_still_changes_nothing() -> None:
    storage = new_database_path()
    conversation, pending, _ = ask_in_one_host(storage, CANCEL)

    after, principal = host(storage=storage)
    with TestClient(after) as client:
        principal.switch_to("CUST-1002")
        assert decide(client, conversation, pending, "approve")[0] == 404
        principal.switch_to("CUST-1001")
        status, problem = decide(client, conversation, {**pending, "operation_id": "op_0000000000000000"}, "approve")
        assert (status, problem["code"]) == (409, "approval_mismatch")
        assert state_of(after, "cancel_order") == "processing"

        status, rejected = decide(client, conversation, pending, "reject")
        assert status == 200
        assert "Nothing has been changed" in rejected["reply"]["text"]
        status, conflict = decide(client, conversation, pending, "approve")
        assert (status, conflict["code"]) == (409, "approval_already_decided")
        assert operation_status(client, pending["operation_id"]) == "rejected"
        assert state_of(after, "cancel_order") == "processing"


def test_an_approval_that_expired_while_the_service_was_down_cannot_run() -> None:
    storage = new_database_path()
    conversation, pending, _ = ask_in_one_host(storage, CANCEL)

    after, _ = host(storage=storage)
    let_expire(after, pending["operation_id"])  # its window passed while no host was running
    with TestClient(after) as client:
        assert client.get(f"/api/v1/conversations/{conversation}").json()["pending_approvals"] == []
        status, problem = decide(client, conversation, pending, "approve")
        assert (status, problem["code"]) == (410, "approval_expired")
        assert operation_status(client, pending["operation_id"]) == "expired"
        assert state_of(after, "cancel_order") == "processing"


async def async_ask(client: httpx.AsyncClient, conversation: str, message: str) -> dict[str, Any]:
    response = await client.post(
        f"/api/v1/conversations/{conversation}/turns", json={"client_turn_id": str(uuid.uuid4()), "message": message}
    )
    assert response.status_code == 200, response.text
    return response.json()


async def async_decide(
    client: httpx.AsyncClient, conversation: str, pending: dict[str, Any], decision: str
) -> httpx.Response:
    return await client.post(
        f"/api/v1/conversations/{conversation}/approvals/{pending['approval_id']}",
        json={"operation_id": pending["operation_id"], "decision": decision},
    )


def asgi(app: FastAPI) -> httpx.AsyncClient:
    return httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test")


@pytest.mark.anyio
async def test_duplicate_concurrent_approvals_produce_one_mutation() -> None:
    app, _ = host()
    async with asgi(app) as client:
        conversation = (await client.post("/api/v1/conversations", json={})).json()["conversation_id"]
        pending = (await async_ask(client, conversation, TICKET))["pending_approval"]

        decisions = await asyncio.gather(*(async_decide(client, conversation, pending, "approve") for _ in range(5)))

    assert [d.status_code for d in decisions] == [200] * 5
    assert len({d.json()["turn_id"] for d in decisions}) == 1
    assert state_of(app, "create_ticket") == "tickets=1"


@pytest.mark.anyio
async def test_two_hosts_on_one_file_approving_at_once_write_once_and_agree_on_one_turn() -> None:
    # Beyond .NET's suite: two hosts share no in-process claim, so only the stored state can stop a second write. The
    # first stored decision wins, the write and its commit are one transaction, and the first stored turn wins.
    storage = new_database_path()
    conversation, pending, _ = ask_in_one_host(storage, TICKET)
    first, _ = host(storage=storage)
    second, _ = host(storage=storage)
    async with asgi(first) as a, asgi(second) as b:
        decisions = await asyncio.gather(
            *(async_decide(client, conversation, pending, "approve") for client in (a, b, a, b))
        )

    assert [d.status_code for d in decisions] == [200] * 4
    assert len({d.json()["turn_id"] for d in decisions}) == 1
    assert state_of(first, "create_ticket") == "tickets=1"
    assert first.state.operations.store.find(pending["operation_id"]).status is Status.COMMITTED


def test_a_decision_read_before_another_host_decided_cannot_overwrite_it(monkeypatch: pytest.MonkeyPatch) -> None:
    # The second host read the approval while it was still awaiting a decision, then the first host approved it. The
    # decision is guarded on the stored status, so the late rejection conflicts instead of overwriting the approval.
    storage = new_database_path()
    conversation, pending, _ = ask_in_one_host(storage, CANCEL)
    first, _ = host(storage=storage)
    second, _ = host(storage=storage)
    late_store = second.state.operations.store
    read_early = late_store.find_by_approval(pending["approval_id"])
    assert read_early is not None

    def stale(_: str) -> Operation:
        return Operation.from_json(read_early.to_json())

    monkeypatch.setattr(late_store, "find_by_approval", stale)

    with TestClient(second) as two:
        assert two.get(f"/api/v1/conversations/{conversation}").status_code == 200  # loaded before the decision
        with TestClient(first) as one:
            assert decide(one, conversation, pending, "approve")[0] == 200
        status, problem = decide(two, conversation, pending, "reject")

    assert (status, problem["code"]) == (409, "approval_already_decided")
    stored = first.state.operations.store.find(pending["operation_id"])
    assert stored is not None
    assert (stored.status, stored.decision) == (Status.COMMITTED, Decision.APPROVE)
    assert state_of(first, "cancel_order") == "cancelled"


@pytest.mark.anyio
@pytest.mark.parametrize(("message", "kind"), ALL_WRITES)
async def test_a_write_committed_just_before_a_crash_is_never_applied_again(message: str, kind: str) -> None:
    # The first host commits the write, then hangs in the model call that follows the tool result, so its decision turn
    # is never stored: as if the process died right after the commit. A new host then gets the same decision again.
    storage = new_database_path()
    conversation, pending, _ = ask_in_one_host(storage, message)
    crashed, _ = host(storage=storage)
    committed, release = asyncio.Event(), asyncio.Event()

    async def hang_after_commit() -> None:
        operation = crashed.state.operations.store.find(pending["operation_id"])
        if operation is not None and operation.status is Status.COMMITTED:
            committed.set()
            await release.wait()
            raise RuntimeError("The host was stopped.")

    model_of(crashed).before_call = hang_after_commit
    async with asgi(crashed) as stuck_client:
        abandoned = asyncio.create_task(async_decide(stuck_client, conversation, pending, "approve"))
        await asyncio.wait_for(committed.wait(), 10)

        restarted, _ = host(storage=storage)
        async with asgi(restarted) as client:
            retried = await async_decide(client, conversation, pending, "approve")
            body = retried.json()
            assert retried.status_code == 200
            assert body["reply"]["kind"] == "approval_result"
            assert body["reply"]["text"].startswith("Done.")  # from the stored record
            assert model_of(restarted).calls == 0  # no model call: nothing was resumed
            operation = (await client.get(f"/api/v1/operations/{pending['operation_id']}")).json()
            assert operation["status"] == "committed"
            assert state_of(restarted, kind) == AFTER[kind]

        # The stuck host finally finishes: its turn loses to the one already stored, so it answers with that one.
        release.set()
        late = await asyncio.wait_for(abandoned, 10)
    assert late.status_code == 200
    assert late.json()["turn_id"] == body["turn_id"]
    assert state_of(crashed, kind) == AFTER[kind]


def test_an_approved_write_that_never_ran_before_a_crash_is_resumed_once() -> None:
    # The decision was stored, then the process stopped before the resumed run wrote anything. Nothing was applied
    # (the write and its commit are one transaction), so the retried decision resumes the run, and it commits once.
    storage = new_database_path()
    conversation, pending, _ = ask_in_one_host(storage, TICKET)
    crashed, _ = host(storage=storage)
    store = crashed.state.operations.store
    operation = store.find(pending["operation_id"])
    assert store.decide(operation, Decision.APPROVE, None, datetime.now(UTC))

    restarted, _ = host(storage=storage)
    with TestClient(restarted) as client:
        status, decided = decide(client, conversation, pending, "approve")
        again, replayed = decide(client, conversation, pending, "approve")
        assert (status, again) == (200, 200)
        assert decided["reply"]["text"].startswith("Done:")  # the model's own words after the tool ran
        assert operation_status(client, pending["operation_id"]) == "committed"
        assert state_of(restarted, "create_ticket") == "tickets=1"
        assert replayed["turn_id"] == decided["turn_id"]


def committed_ticket(app: FastAPI) -> tuple[Operation, dict[str, Any]]:
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        pending = api.ask(conversation, TICKET)["pending_approval"]
        decide(client, conversation, pending, "approve")
    operation = app.state.operations.store.find(pending["operation_id"])
    assert operation is not None
    assert operation.result is not None
    return operation, operation.result


def test_a_stale_run_of_a_finished_operation_reports_the_stored_result_and_writes_nothing() -> None:
    app, _ = host()
    committed, result = committed_ticket(app)
    stale = Operation.from_json(committed.to_json())
    stale.status, stale.result = Status.RUNNING, None  # a copy still believing it is running

    with pinned(Principal("CUST-1001")), approval_scope(stale):
        got = app.state.operations.execute_approved(Principal("CUST-1001"), Kind.CREATE_TICKET, stale.arguments)

    assert got == result
    assert stale.status is Status.COMMITTED
    assert state_of(app, "create_ticket") == "tickets=1"


def test_a_write_whose_guarded_commit_loses_is_rolled_back(monkeypatch: pytest.MonkeyPatch) -> None:
    # The stored status is read as "running" (as if read just before another run committed), so the ticket insert
    # happens; then the guarded commit finds the operation already committed. Both are one transaction: the ticket
    # insert rolls back, and the stored result is reported.
    app, _ = host()
    committed, result = committed_ticket(app)
    store = app.state.operations.store
    running = Operation.from_json(committed.to_json())
    running.status, running.result = Status.RUNNING, None
    real_find = store.find
    reads: list[str] = []

    def first_read_is_stale(operation_id: str) -> Operation | None:
        reads.append(operation_id)
        return Operation.from_json(running.to_json()) if len(reads) == 1 else real_find(operation_id)

    monkeypatch.setattr(store, "find", first_read_is_stale)
    stale = Operation.from_json(running.to_json())

    with pinned(Principal("CUST-1001")), approval_scope(stale):
        got = app.state.operations.execute_approved(Principal("CUST-1001"), Kind.CREATE_TICKET, stale.arguments)

    monkeypatch.undo()
    assert got == result
    assert state_of(app, "create_ticket") == "tickets=1"  # the second ticket was rolled back
    assert database_of(app).query("SELECT COUNT(*) FROM tickets")[0][0] == 1


def test_a_conversation_and_its_specialist_history_survive_a_restart() -> None:
    storage = new_database_path()
    first_key = str(uuid.uuid4())
    before, _ = host(storage=storage)
    with TestClient(before) as client:
        api = Api(client)
        conversation = api.create_conversation()
        _, first = api.send(conversation, "Where is my order ORD-1002?", first_key)

    after, _ = host(storage=storage)
    seen: list[list[Message]] = []

    def recording(messages: Sequence[Message], options: Mapping[str, Any]) -> ChatResponse:
        seen.append(list(messages))
        return default_script(messages, options)

    model_of(after).script = recording
    with TestClient(after) as client:
        api = Api(client)
        follow_up = api.ask(conversation, "When was it shipped?")
        calls = model_of(after).calls
        status, replayed = api.send(conversation, "Where is my order ORD-1002?", first_key)

        # The verified order came back (no "which order?"), and the specialist saw the earlier exchange.
        assert follow_up["reply"]["kind"] == "answer"
        assert "ORD-1002" in follow_up["reply"]["text"]
        assert any(m.role == "user" and m.text == "Where is my order ORD-1002?" for m in seen[0])
        # The first turn replays from storage: same turn, no new model call.
        assert (status, replayed["turn_id"]) == (200, first["turn_id"])
        assert model_of(after).calls == calls


def stop_at(app: FastAPI, when: str) -> None:
    database_of(app).execute(f"CREATE TRIGGER stop_here {when} BEGIN SELECT RAISE(ABORT, 'simulated stop'); END")


def resume_writes(app: FastAPI) -> None:
    database_of(app).execute("DROP TRIGGER stop_here")


def test_an_approval_is_never_listed_without_its_paused_request() -> None:
    # The operation and its native request are one transaction: if storing the request fails, no operation is left
    # behind as an approval nobody could resume. The turn fails as a result (outcome failed), not a server error.
    storage = new_database_path()
    app, _ = host(storage=storage)
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        stop_at(app, "BEFORE INSERT ON native_approval_requests")
        assert api.ask(conversation, CANCEL)["outcome"] == "failed"
        resume_writes(app)

    restarted, _ = host(storage=storage)
    with TestClient(restarted) as client:
        assert client.get(f"/api/v1/conversations/{conversation}").json()["pending_approvals"] == []
        assert restarted.state.operations.store.for_conversation(conversation) == []


def test_an_approved_operation_whose_paused_request_is_missing_fails_instead_of_staying_running() -> None:
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        pending = api.ask(conversation, CANCEL)["pending_approval"]
        database_of(app).execute(
            "DELETE FROM native_approval_requests WHERE operation_id = ?", (pending["operation_id"],)
        )

        status, decided = decide(client, conversation, pending, "approve")

        assert status == 200
        assert "Nothing has been changed" in decided["reply"]["text"]
        assert operation_status(client, pending["operation_id"]) == "failed"
        assert state_of(app, "cancel_order") == "processing"


def post_turn_status(app: FastAPI, conversation: str, client_turn_id: str, message: str) -> int:
    with TestClient(app, raise_server_exceptions=False) as client:
        response = client.post(
            f"/api/v1/conversations/{conversation}/turns", json={"client_turn_id": client_turn_id, "message": message}
        )
        return response.status_code


def test_a_finished_turn_is_never_stored_without_the_context_it_produced() -> None:
    # The turn and the conversation's context are one transaction. If the context update fails, the turn is not stored
    # either, so a retry of the same client_turn_id runs again and sets the context.
    storage = new_database_path()
    client_turn_id = str(uuid.uuid4())
    app, _ = host(storage=storage)
    with TestClient(app) as client:
        conversation = Api(client).create_conversation()
    stop_at(app, "BEFORE UPDATE OF context_json ON conversations")
    assert post_turn_status(app, conversation, client_turn_id, "Where is my order ORD-1002?") == 500
    resume_writes(app)

    restarted, _ = host(storage=storage)
    assert database_of(restarted).query("SELECT COUNT(*) FROM turns")[0][0] == 0
    with TestClient(restarted) as client:
        api = Api(client)
        status, _ = api.send(conversation, "Where is my order ORD-1002?", client_turn_id)
        assert status == 200
        follow_up = api.ask(conversation, "When was it shipped?")
        assert follow_up["reply"]["kind"] == "answer"
        assert "ORD-1002" in follow_up["reply"]["text"]


def test_an_approval_whose_turn_was_never_stored_cannot_run_and_a_retry_asks_once_more() -> None:
    # The proposal was stored, then the process stopped before its turn was. The customer never saw that approval;
    # retrying the same submission asks again, and only the new approval is actionable.
    storage = new_database_path()
    client_turn_id = str(uuid.uuid4())
    app, _ = host(storage=storage)
    with TestClient(app) as client:
        conversation = Api(client).create_conversation()
    stop_at(app, "BEFORE INSERT ON turns")
    assert post_turn_status(app, conversation, client_turn_id, TICKET) == 500
    resume_writes(app)

    restarted, _ = host(storage=storage)
    [orphan] = restarted.state.operations.store.for_conversation(conversation)
    with TestClient(restarted) as client:
        api = Api(client)
        assert client.get(f"/api/v1/conversations/{conversation}").json()["pending_approvals"] == []

        _, asked = api.send(conversation, TICKET, client_turn_id)
        pending = asked["pending_approval"]

        assert pending["operation_id"] != orphan.operation_id
        assert len(client.get(f"/api/v1/conversations/{conversation}").json()["pending_approvals"]) == 1
        assert operation_status(client, orphan.operation_id) == "expired"
        orphan_pending = {"approval_id": orphan.approval_id, "operation_id": orphan.operation_id}
        status, problem = decide(client, conversation, orphan_pending, "approve")
        assert (status, problem["code"]) == (410, "approval_expired")
        decide(client, conversation, pending, "approve")
        assert state_of(restarted, "create_ticket") == "tickets=1"


@pytest.mark.parametrize(
    "failing_write",
    [
        "BEFORE INSERT ON turns",  # storing the decision turn fails (after the write committed)
        "BEFORE UPDATE ON operations",  # recording the decision itself fails
    ],
)
def test_a_decision_that_could_not_be_stored_can_be_retried_and_does_not_block_the_conversation(
    failing_write: str,
) -> None:
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        pending = api.ask(conversation, TICKET)["pending_approval"]
    stop_at(app, failing_write)
    with TestClient(app, raise_server_exceptions=False) as client:
        response = client.post(
            f"/api/v1/conversations/{conversation}/approvals/{pending['approval_id']}",
            json={"operation_id": pending["operation_id"], "decision": "approve"},
        )
        assert response.status_code == 500
    resume_writes(app)

    with TestClient(app) as client:
        api = Api(client)
        status, retried = decide(client, conversation, pending, "approve")
        assert status == 200
        assert retried["reply"]["kind"] == "approval_result"
        assert operation_status(client, pending["operation_id"]) == "committed"
        assert state_of(app, "create_ticket") == "tickets=1"
        assert api.ask(conversation, "Where is my order ORD-1002?")["outcome"] == "completed"


def test_a_turn_that_fails_after_proposing_a_write_leaves_no_actionable_approval() -> None:
    # The proposal is stored, then saving the session fails, so the turn ends failed. Its approval must not stay
    # actionable: the customer was told the turn failed.
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        stop_at(app, "BEFORE INSERT ON agent_sessions")

        failed = api.ask(conversation, CANCEL)

        assert failed["outcome"] == "failed"
        assert client.get(f"/api/v1/conversations/{conversation}").json()["pending_approvals"] == []
        [operation] = app.state.operations.store.for_conversation(conversation)
        assert operation.status is Status.EXPIRED
        pending = {"approval_id": operation.approval_id, "operation_id": operation.operation_id}
        assert decide(client, conversation, pending, "approve")[0] == 410
        assert state_of(app, "cancel_order") == "processing"


def test_hosts_on_different_files_share_nothing() -> None:
    first, _ = host()
    second, _ = host()
    with TestClient(first) as one, TestClient(second) as two:
        conversation = Api(one).create_conversation()
        Api(one).ask(conversation, CANCEL)
        assert two.get(f"/api/v1/conversations/{conversation}").status_code == 404
        assert state_of(second, "cancel_order") == "processing"
    assert database_of(first).path != database_of(second).path


def test_the_python_backend_has_its_own_default_file() -> None:
    from support_agent.settings import StorageSettings

    assert StorageSettings().path == Path(".data") / "python" / "support.db"  # never the .NET backend's file


def test_shutdown_closes_the_database_and_leaves_no_write_ahead_log_behind() -> None:
    storage = new_database_path()
    app, _ = host(storage=storage)
    with TestClient(app) as client:
        Api(client).create_conversation()
        assert not app.state.resources.closed
    assert "database" in app.state.resources.closed
    wal = storage.with_name(storage.name + "-wal")
    assert not wal.exists() or wal.stat().st_size == 0
    with sqlite3.connect(storage) as connection:  # the file is complete on its own
        assert connection.execute("SELECT COUNT(*) FROM conversations").fetchone()[0] == 1


def test_every_stored_turn_still_matches_the_contract_after_a_restart() -> None:
    storage = new_database_path()
    conversation, pending, asked_turn_id = ask_in_one_host(storage, CANCEL)
    after, _ = host(storage=storage)
    with TestClient(after) as client:
        turn = client.get(f"/api/v1/conversations/{conversation}/turns/{asked_turn_id}").json()
        assert_contract("turn-result", turn)
        assert turn["outcome"] == "awaiting_approval"
        assert turn["pending_approval"]["operation_id"] == pending["operation_id"]
