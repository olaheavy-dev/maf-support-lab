"""Shared test helpers: a host as a given customer, and contract checks on every response."""

from __future__ import annotations

import asyncio
import json
import tempfile
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any

from fastapi import FastAPI
from fastapi.testclient import TestClient

from support_agent import contract
from support_agent.app import create_app, get_principal_source
from support_agent.identity import DevelopmentPrincipalSource, PrincipalSource
from support_agent.settings import Environment, IdentitySettings, ResilienceSettings, Settings, StorageSettings


class SwitchablePrincipal:
    """A server-side principal source the test can switch (the trusted test-host injection point)."""

    def __init__(self, customer_id: str) -> None:
        self.source: PrincipalSource = DevelopmentPrincipalSource(customer_id)

    def switch_to(self, customer_id: str) -> None:
        self.source = DevelopmentPrincipalSource(customer_id)


def new_database_path() -> Path:
    """A database file of its own (L38): no test ever shares state with another, or with a developer's local file."""
    return Path(tempfile.mkdtemp(prefix="support-test-")) / "support.db"


def host(
    customer: str = "CUST-1001", storage: Path | None = None, resilience: ResilienceSettings | None = None
) -> tuple[FastAPI, SwitchablePrincipal]:
    """A host as ``customer``. Pass the same ``storage`` to a second host to simulate a restart on the same file, and
    ``resilience`` to tighten a turn's limits (L42)."""
    settings = Settings(
        app_environment=Environment.DEVELOPMENT,
        identity=IdentitySettings(development_customer_id="CUST-1001"),
        storage=StorageSettings(path=storage or new_database_path()),
        resilience=resilience or ResilienceSettings(),
    )
    app = create_app(settings)
    principal = SwitchablePrincipal(customer)
    app.dependency_overrides[get_principal_source] = lambda: principal.source
    return app, principal


def assert_contract(schema: str, document: Any) -> None:
    found = contract.errors(schema, document)
    assert not found, f"{schema} contract violations: {found}"


class Api:
    def __init__(self, client: TestClient) -> None:
        self.client = client

    def create_conversation(self) -> str:
        response = self.client.post("/api/v1/conversations", json={})
        assert response.status_code == 201
        assert_contract("conversation", response.json())
        return str(response.json()["conversation_id"])

    def send(self, conversation: str, message: str, client_turn_id: str | None = None) -> tuple[int, dict[str, Any]]:
        response = self.client.post(
            f"/api/v1/conversations/{conversation}/turns",
            json={"client_turn_id": client_turn_id or str(uuid.uuid4()), "message": message},
        )
        body: dict[str, Any] = response.json()
        assert_contract("turn-result" if response.status_code == 200 else "problem", body)
        return response.status_code, body

    def ask(self, conversation: str, message: str, client_turn_id: str | None = None) -> dict[str, Any]:
        status, body = self.send(conversation, message, client_turn_id)
        assert status == 200, body
        return body


def let_expire(app: FastAPI, operation_id: str) -> None:
    """Moves a stored approval's deadline into the past (as if its window passed, also while the service was down)."""
    store = app.state.operations.store
    operation = store.find(operation_id)
    assert operation is not None
    operation.expires_at = datetime.now(UTC) - timedelta(seconds=1)
    assert store.try_update(operation, operation.status)


def execute(operations: Any, *arguments: Any) -> dict[str, Any]:
    """Runs ``OperationService.execute_approved`` (async since L20) from synchronous test code."""
    result: dict[str, Any] = asyncio.run(operations.execute_approved(*arguments))
    return result


SSE = {"Accept": "text/event-stream"}


def parse_sse(text: str) -> list[dict[str, Any]]:
    """Parses a whole SSE body. Every event must match the stream-event schema, carry SSE ``id`` = sequence and
    ``event`` = type, and the sequence must start at 1 and rise by 1. Comment lines (keepalives) are skipped."""
    events: list[dict[str, Any]] = []
    for block in text.replace("\r\n", "\n").strip().split("\n\n"):
        fields = dict(line.split(": ", 1) for line in block.splitlines() if line and not line.startswith(":"))
        if not fields:
            continue
        event: dict[str, Any] = json.loads(fields["data"])
        assert_contract("stream-event", event)
        assert fields["event"] == event["type"]
        assert fields["id"] == str(event["sequence"])
        events.append(event)
    assert [e["sequence"] for e in events] == list(range(1, len(events) + 1))
    return events


def final_of(events: list[dict[str, Any]]) -> dict[str, Any]:
    """The one terminal event's result; it must be the last event and the only final one."""
    finals = [e for e in events if e["type"] == "turn.final"]
    assert len(finals) == 1
    assert events[-1] is finals[0]
    assert events[0]["type"] == "turn.started"
    result: dict[str, Any] = finals[0]["data"]["result"]
    return result


def streamed_text(events: list[dict[str, Any]]) -> str:
    """The contract's text rule: the deltas after the last reset concatenate to the final reply text."""
    last_reset = max((i for i, e in enumerate(events) if e["type"] == "reply.reset"), default=-1)
    return "".join(e["data"]["text"] for e in events[last_reset + 1 :] if e["type"] == "reply.delta")


def stages(events: list[dict[str, Any]]) -> list[str]:
    return [e["data"]["stage"] for e in events if e["type"] == "turn.progress"]
