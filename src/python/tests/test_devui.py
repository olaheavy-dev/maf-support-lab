"""Development-only DevUI lists only the routing workflow, which goes through triage, the approval gate and the same guards."""

from __future__ import annotations

from collections.abc import Generator
from typing import Any

import pytest
from agent_framework_devui import DevServer
from fastapi.testclient import TestClient

from support_agent.devui import entities
from support_agent.identity import set_process_principal
from support_agent.settings import Environment, IdentitySettings, Settings


@pytest.fixture
def devui() -> Generator[TestClient]:
    server = DevServer(host="127.0.0.1", auth_enabled=False, ui_enabled=False)
    server.register_entities(
        entities(
            Settings(
                app_environment=Environment.DEVELOPMENT, identity=IdentitySettings(development_customer_id="CUST-1001")
            )
        )
    )
    with TestClient(server.get_app(), base_url="http://127.0.0.1") as client:
        yield client
    set_process_principal(None)


def listed(client: TestClient) -> dict[str, dict[str, Any]]:
    return {entity["name"]: entity for entity in client.get("/v1/entities").json()["entities"]}


def ask(client: TestClient, entity_id: str, text: str) -> str:
    response = client.post("/v1/responses", json={"metadata": {"entity_id": entity_id}, "input": text, "stream": False})
    assert response.status_code == 200, response.text
    return response.text


def test_devui_lists_only_the_workflow(devui: TestClient) -> None:
    # A specialist listed on its own would skip triage and the approval gate (see support_agent.devui).
    found = listed(devui)
    assert set(found) == {"support-routing"}
    assert found["support-routing"]["type"] == "workflow"


@pytest.mark.parametrize(
    ("message", "route", "expected"),
    [
        ("Can you send me the invoice for ORD-1001?", "[route: billing | reply: answer]", "INV-5001"),
        ("What's the status of ORD-2001?", "[route: orders | reply: not_found]", "ORD-2001"),
        (
            "asdf qwer zxcv",
            "[route: unrouted (ambiguous_request) | reply: clarification]",
            "order, billing, or a technical problem",
        ),
    ],
)
def test_a_devui_workflow_chat_goes_through_triage_and_the_same_guards(
    devui: TestClient, message: str, route: str, expected: str
) -> None:
    text = ask(devui, listed(devui)["support-routing"]["id"], message)
    assert route in text
    assert expected in text


@pytest.mark.parametrize("customer", [None, "CUST-1001"])
def test_devui_refuses_to_start_outside_development(customer: str | None) -> None:
    # Even with a customer configured, production never gets DevUI.
    with pytest.raises(RuntimeError, match="only available in development"):
        entities(
            Settings(
                app_environment=Environment.PRODUCTION, identity=IdentitySettings(development_customer_id=customer)
            )
        )


def test_devui_rejects_requests_not_addressed_to_loopback() -> None:
    # DevUI's own guard against DNS rebinding: a request addressed to another host name is refused.
    server = DevServer(host="127.0.0.1", auth_enabled=False, ui_enabled=False)
    server.register_entities(
        entities(
            Settings(
                app_environment=Environment.DEVELOPMENT, identity=IdentitySettings(development_customer_id="CUST-1001")
            )
        )
    )
    with TestClient(server.get_app(), base_url="http://attacker.example") as client:
        assert client.get("/v1/entities").status_code == 400
    set_process_principal(None)


@pytest.mark.parametrize(
    ("message", "route"),
    [
        ("Please cancel order ORD-1003.", "[route: orders | reply: clarification]"),
        ("Refund ORD-2003 to my card right away.", "[route: billing | reply: clarification]"),  # another customer's
    ],
)
def test_a_devui_workflow_chat_cannot_request_a_change(devui: TestClient, message: str, route: str) -> None:
    # DevUI workflow chats are stateless (no conversation), so a write request is declined natively: nothing changes.
    text = ask(devui, listed(devui)["support-routing"]["id"], message)
    assert route in text
    assert "reply: approval_request" not in text
