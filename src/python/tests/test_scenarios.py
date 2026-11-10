"""L18: the shared read-only feature scenarios (S01-S09), straight from fixtures/scenarios/feature-scenarios.json.

The same file drives the .NET backend, so both implementations are held to one description of behaviour. Every turn
result is checked against the contract.
"""

from __future__ import annotations

import json
from typing import Any

import pytest
from fastapi.testclient import TestClient

from support_agent.fixtures import REPOSITORY_ROOT
from tests.helpers import Api, host

SCENARIOS: list[dict[str, Any]] = json.loads(
    (REPOSITORY_ROOT / "fixtures" / "scenarios" / "feature-scenarios.json").read_text(encoding="utf-8")
)["scenarios"]
READ_ONLY = [
    scenario
    for scenario in SCENARIOS
    if scenario["expected_state_change"]["type"] == "none"
    and all(step.get("action") == "send" for step in scenario["steps"])
][:9]


def test_the_read_only_scenarios_are_s01_to_s09() -> None:
    assert [scenario["scenario_id"][:3] for scenario in READ_ONLY] == [f"S0{n}" for n in range(1, 10)]


@pytest.mark.parametrize("scenario", READ_ONLY, ids=[scenario["scenario_id"] for scenario in READ_ONLY])
def test_scenario_behaves_as_described(scenario: dict[str, Any]) -> None:
    app, _ = host(scenario["principal"])
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        for step in scenario["steps"]:
            turn = api.ask(conversation, step["message"])
            expect = step["expect"]
            route = turn["route"]
            if "route_status" in expect:
                assert route["status"] == expect["route_status"]
            if "route_target" in expect:
                assert route.get("target") == expect["route_target"]
            assert turn["outcome"] == expect["outcome"]
            assert turn["reply"]["kind"] == expect["reply_kind"]
            for mention in expect.get("mentions", []):
                assert mention.lower() in turn["reply"]["text"].lower()
            for forbidden in expect.get("must_not_mention", []):
                assert forbidden.lower() not in turn["reply"]["text"].lower()
