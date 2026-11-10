"""L18: the tools read only the pinned principal's records; anything else looks missing, and nothing can write."""

from __future__ import annotations

import re
from collections.abc import Generator
from typing import Any, cast

import pytest

from support_agent.agents import SPECIALISTS, create_agents
from support_agent.fake_model import FakeChatClient
from support_agent.fixtures import Fixtures
from support_agent.identity import Principal, pinned, set_process_principal
from support_agent.services import Services
from support_agent.tools import billing_tools, order_tools, technical_tools

SERVICES = Services.from_fixtures(Fixtures.load())
WRITE_VERBS = re.compile(
    r"^(cancel|refund|create|update|delete|issue|change|approve|open|pay|set|send)_", re.IGNORECASE
)


@pytest.fixture(autouse=True)
def no_process_principal() -> Generator[None]:
    set_process_principal(None)
    yield
    set_process_principal(None)


def call(tool: Any, **arguments: Any) -> dict[str, Any]:
    result: dict[str, Any] = tool.func(**arguments)
    return result


def test_another_customers_order_looks_exactly_like_a_missing_one() -> None:
    (get_order_status,) = order_tools(SERVICES)
    with pinned(Principal("CUST-1001")):
        foreign = call(get_order_status, order_id="ORD-2001")
        missing = call(get_order_status, order_id="ORD-9999")
        own = call(get_order_status, order_id="ORD-1002")
    assert foreign == {"lookup": "not_found", "order_id": "ORD-2001"}
    assert missing == {"lookup": "not_found", "order_id": "ORD-9999"}
    assert own["lookup"] == "found"


def test_billing_never_returns_another_customers_invoice_or_payments() -> None:
    get_invoice, get_payment_history = billing_tools(SERVICES)
    with pinned(Principal("CUST-1001")):
        assert call(get_invoice, reference="INV-5004")["lookup"] == "not_found"  # CUST-1002's invoice
        assert call(get_invoice, reference="ORD-2001")["lookup"] == "not_found"
        assert call(get_invoice, reference="INV-5001")["lookup"] == "found"
        payments = call(get_payment_history)["payments"]
    assert payments
    assert all(payment["order_id"].startswith("ORD-1") for payment in payments)


def test_invalid_ids_are_rejected_before_any_lookup() -> None:
    (get_order_status,) = order_tools(SERVICES)
    get_invoice, _ = billing_tools(SERVICES)
    with pinned(Principal("CUST-1001")):
        assert call(get_order_status, order_id="ORD-1002; drop table orders")["lookup"] == "invalid_order_id"
        assert call(get_invoice, reference="1001")["lookup"] == "invalid_reference"


def test_tools_fail_closed_when_no_principal_is_pinned() -> None:
    (get_order_status,) = order_tools(SERVICES)
    with pytest.raises(RuntimeError, match="fail closed"):
        call(get_order_status, order_id="ORD-1002")


def test_no_tool_takes_a_customer_id_or_a_confirmation() -> None:
    for tool in [*order_tools(SERVICES), *billing_tools(SERVICES), *technical_tools(SERVICES)]:
        parameters = set(tool.parameters().get("properties", {}))
        assert not {name for name in parameters if "customer" in name or "confirm" in name}, tool.name


def test_the_agents_have_only_read_tools_and_triage_has_none() -> None:
    agents = create_agents(FakeChatClient(), SERVICES)
    tools: dict[str, list[str]] = {
        name: tool_names(agents.specialist(name).default_options.get("tools")) for name in SPECIALISTS
    }
    assert tools == {
        "orders": ["get_order_status"],
        "billing": ["get_invoice", "get_payment_history"],
        "technical": ["search_knowledge_base"],
    }
    assert not agents.triage.default_options.get("tools")
    assert not [name for names in tools.values() for name in names if WRITE_VERBS.search(name)]


def tool_names(tools: Any) -> list[str]:
    return [str(getattr(tool, "name", "")) for tool in cast(list[Any], tools or [])]


def test_the_knowledge_search_is_bounded_and_matches_keywords_only() -> None:
    (search,) = technical_tools(SERVICES)
    assert call(search, query="headphones crackling")["articles"][0]["article_id"] == "KB-102"
    assert call(search, query="smart fridge")["search"] == "no_match"  # "Smart Speaker" is a title, not a keyword
    assert call(search, query="x" * 201)["search"] == "invalid_query"
