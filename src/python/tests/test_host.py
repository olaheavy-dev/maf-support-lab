"""The host serves health without credentials, identity comes only from the server, and resources are closed."""

from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

from support_agent.app import create_app, get_principal_source
from support_agent.identity import DevelopmentPrincipalSource
from support_agent.settings import Environment, IdentitySettings, Settings


def development(customer: str = "CUST-1001") -> Settings:
    return Settings(
        app_environment=Environment.DEVELOPMENT, identity=IdentitySettings(development_customer_id=customer)
    )


def test_health_is_healthy_in_fake_mode_without_secrets() -> None:
    with TestClient(create_app(development())) as client:
        assert client.get("/health").text == "Healthy"
        assert client.get("/alive").text == "Healthy"


def test_the_development_principal_comes_from_server_configuration() -> None:
    with TestClient(create_app(development("CUST-1001"))) as client:
        assert client.get("/dev/whoami").json() == {"customer_id": "CUST-1001"}


@pytest.mark.parametrize("header", ["X-Customer-Id", "X-User-Id", "X-Principal", "Authorization"])
def test_client_headers_cannot_change_the_principal(header: str) -> None:
    with TestClient(create_app(development("CUST-1001"))) as client:
        response = client.get("/dev/whoami", headers={header: "CUST-1002"})
        assert response.json() == {"customer_id": "CUST-1001"}


def test_the_query_string_cannot_change_the_principal() -> None:
    with TestClient(create_app(development("CUST-1001"))) as client:
        assert client.get("/dev/whoami?customer_id=CUST-1002").json() == {"customer_id": "CUST-1001"}


def test_the_test_host_can_run_as_another_customer_through_a_server_side_override() -> None:
    app = create_app(development("CUST-1001"))
    app.dependency_overrides[get_principal_source] = lambda: DevelopmentPrincipalSource("CUST-1002")
    with TestClient(app) as client:
        assert client.get("/dev/whoami").json() == {"customer_id": "CUST-1002"}


def test_outside_development_there_is_no_whoami_and_no_identity_provider() -> None:
    app = create_app(Settings(app_environment=Environment.PRODUCTION))
    with TestClient(app) as client:
        assert client.get("/dev/whoami").status_code == 404
        assert client.get("/health").text == "Healthy"
    with pytest.raises(RuntimeError, match="No identity provider"):
        app.state.principal_source.current()


def test_owned_resources_are_closed_once_newest_first_at_shutdown() -> None:
    app = create_app(development())
    order: list[str] = []

    async def close_first() -> None:
        order.append("client")

    async def close_second() -> None:
        order.append("database")

    with TestClient(app):
        app.state.resources.own("client", close_first)
        app.state.resources.own("database", close_second)
    assert order == ["database", "client"]
