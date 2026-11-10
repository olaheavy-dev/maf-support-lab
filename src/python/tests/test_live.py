"""Opt-in live checks against Claude (L18): never in CI or a normal run. `uv run pytest -m live` with ANTHROPIC_API_KEY.

The shared read-only scenarios run through the Python API with the real model (claude-haiku-5-5), so the same tools,
guards, triage validation, and routing hold with a real model, not only with the fake.
"""

from __future__ import annotations

import json
import os
from typing import Any

import pytest
from fastapi.testclient import TestClient
from pydantic import SecretStr

from support_agent.app import create_app, get_principal_source
from support_agent.fixtures import REPOSITORY_ROOT
from support_agent.identity import DevelopmentPrincipalSource
from support_agent.settings import (
    AnthropicSettings,
    Environment,
    IdentitySettings,
    ModelMode,
    ModelProviderSettings,
    Settings,
)
from tests.helpers import Api

pytestmark = pytest.mark.live
# Read once at import: the hermetic autouse fixture clears provider variables before each test runs.
LIVE_KEY = os.environ.get("ANTHROPIC_API_KEY")
SCENARIOS: list[dict[str, Any]] = json.loads(
    (REPOSITORY_ROOT / "fixtures" / "scenarios" / "feature-scenarios.json").read_text(encoding="utf-8")
)["scenarios"]
LIVE = [s for s in SCENARIOS if s["scenario_id"][:3] in {"S01", "S03", "S04", "S05", "S06", "S07", "S09"}]


@pytest.mark.parametrize("scenario", LIVE, ids=[s["scenario_id"] for s in LIVE])
def test_claude_behaves_as_the_shared_scenario_describes(scenario: dict[str, Any]) -> None:
    key = LIVE_KEY
    if not key:
        pytest.skip("Set ANTHROPIC_API_KEY to run live checks.")
    settings = Settings(
        app_environment=Environment.DEVELOPMENT,
        identity=IdentitySettings(development_customer_id="CUST-1001"),
        model_provider=ModelProviderSettings(
            mode=ModelMode.LIVE, anthropic=AnthropicSettings(model="claude-haiku-5-5", api_key=SecretStr(key))
        ),
    )
    app = create_app(settings)
    app.dependency_overrides[get_principal_source] = lambda: DevelopmentPrincipalSource(scenario["principal"])
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        step = scenario["steps"][0]
        turn = api.ask(conversation, step["message"])
        expect = step["expect"]
        print(f"{scenario['scenario_id']}: {turn['route']} {turn['reply']['kind']}: {turn['reply']['text'][:160]}")
        if "route_target" in expect:
            assert turn["route"].get("target") == expect["route_target"]
        if "route_status" in expect:
            assert turn["route"]["status"] == expect["route_status"]
        assert turn["reply"]["kind"] == expect["reply_kind"]
        for mention in expect.get("mentions", []):
            assert mention.lower() in turn["reply"]["text"].lower()
        for forbidden in expect.get("must_not_mention", []):
            assert forbidden.lower() not in turn["reply"]["text"].lower()


def test_claude_answers_a_follow_up_from_the_conversations_own_session() -> None:
    key = LIVE_KEY
    if not key:
        pytest.skip("Set ANTHROPIC_API_KEY to run live checks.")
    settings = Settings(
        app_environment=Environment.DEVELOPMENT,
        identity=IdentitySettings(development_customer_id="CUST-1001"),
        model_provider=ModelProviderSettings(
            mode=ModelMode.LIVE, anthropic=AnthropicSettings(model="claude-haiku-5-5", api_key=SecretStr(key))
        ),
    )
    with TestClient(create_app(settings)) as client:
        api = Api(client)
        conversation = api.create_conversation()
        api.ask(conversation, "Where is my order ORD-1002?")
        follow_up = api.ask(conversation, "When was it shipped?")  # no id: the verified order and the session carry it
        invoice = api.ask(conversation, "Can I get the invoice for it?")
        print(f"follow-up: {follow_up['route']} {follow_up['reply']['kind']}: {follow_up['reply']['text'][:160]}")
        print(f"invoice: {invoice['route']} {invoice['reply']['kind']}: {invoice['reply']['text'][:160]}")
        assert follow_up["route"]["target"] == "orders"
        assert follow_up["reply"]["kind"] == "answer"
        assert "30" in follow_up["reply"]["text"]  # shipped on October 30
        assert invoice["route"]["target"] == "billing"
        assert "INV-5002" in invoice["reply"]["text"]
