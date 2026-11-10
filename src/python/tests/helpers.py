"""Shared test helpers: a host as a given customer, and contract checks on every response."""

from __future__ import annotations

import uuid
from typing import Any

from fastapi import FastAPI
from fastapi.testclient import TestClient

from support_agent import contract
from support_agent.app import create_app, get_principal_source
from support_agent.identity import DevelopmentPrincipalSource, PrincipalSource
from support_agent.settings import Environment, IdentitySettings, Settings


class SwitchablePrincipal:
    """A server-side principal source the test can switch (the trusted test-host injection point)."""

    def __init__(self, customer_id: str) -> None:
        self.source: PrincipalSource = DevelopmentPrincipalSource(customer_id)

    def switch_to(self, customer_id: str) -> None:
        self.source = DevelopmentPrincipalSource(customer_id)


def host(customer: str = "CUST-1001") -> tuple[FastAPI, SwitchablePrincipal]:
    app = create_app(
        Settings(
            app_environment=Environment.DEVELOPMENT, identity=IdentitySettings(development_customer_id="CUST-1001")
        )
    )
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
