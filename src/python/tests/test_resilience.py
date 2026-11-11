"""L42: bounded retries, timeouts, and execution budgets (porting .NET L15's ResilienceTests).

The SDK-level tests run the **real Anthropic SDK** in live mode over a fake HTTP API (``tests/fake_anthropic.py``), so
its actual retry loop, backoff, ``retry-after`` handling and attempt timeouts are what's tested. The rest use the fake
model. No test makes a network call.

The rules under test:
- the SDK is the one retry owner: exactly ``model_max_retries + 1`` attempts per model call, and nothing above it retries;
- a turn has a model-call budget, a tool-loop limit, a per-tool time limit, and a deadline;
- every failure is a stored ``failed`` result with a classified code and fixed, safe text;
- a timeout never rolls a write back, and never lets one apply twice.
"""

from __future__ import annotations

import asyncio
import time
import uuid
from collections.abc import Mapping, Sequence
from pathlib import Path
from typing import Any

import anthropic
import httpx
import httpx2
import pytest
from agent_framework import ChatResponse, Message
from agent_framework.exceptions import ChatClientException
from fastapi import FastAPI
from fastapi.testclient import TestClient
from pydantic import ValidationError

from support_agent import resilience
from support_agent.app import create_app, get_principal_source
from support_agent.fake_model import ScriptedChatClient, default_script, tool_call
from support_agent.identity import DevelopmentPrincipalSource
from support_agent.services import OrderService
from support_agent.settings import ResilienceSettings, Settings
from tests.conftest import SPANS
from tests.fake_anthropic import REPLY, Attempt, FakeAnthropicHttp, Fault, live_settings
from tests.helpers import SSE, Api, assert_contract, final_of, host, parse_sse
from tests.test_approvals import AFTER, BEFORE, decide, state_of

ORDER_QUESTION = "Where is ORD-1002?"


@pytest.fixture
def anyio_backend() -> str:
    return "asyncio"


def live_host(fake: FakeAnthropicHttp, storage: Path | None = None, **limits: Any) -> FastAPI:
    app = create_app(live_settings(storage, **limits), provider_http_client=fake.client)
    app.dependency_overrides[get_principal_source] = lambda: DevelopmentPrincipalSource("CUST-1001")
    return app


def one_turn(app: FastAPI, message: str = ORDER_QUESTION) -> dict[str, Any]:
    with TestClient(app) as client:
        api = Api(client)
        return api.ask(api.create_conversation(), message)


def turn_span(trace_id: str) -> Any:
    found = [
        span
        for span in SPANS.get_finished_spans()
        if span.name == "support.turn" and span.context is not None and f"{span.context.trace_id:032x}" == trace_id
    ]
    assert len(found) == 1, f"expected one support.turn span for trace {trace_id}"
    return found[0]


def model(app: FastAPI) -> ScriptedChatClient:
    return app.state.agents.triage.client


def failed_with(result: dict[str, Any], code: str) -> dict[str, Any]:
    assert result["outcome"] == "failed", result
    assert "reply" not in result
    assert result["error"]["code"] == code
    return result["error"]


# ---- One retry owner: the SDK (real SDK, fake HTTP) -------------------------------------------------------------------


def test_transient_provider_errors_are_retried_by_the_sdk_and_the_turn_completes() -> None:
    fake = FakeAnthropicHttp(lambda _attempt, number: Fault(500) if number <= 2 else None)
    result = one_turn(live_host(fake))
    assert result["outcome"] == "completed"
    assert result["reply"]["text"] == REPLY
    assert len(fake.triage_attempts) == 3  # two 500s, then success: the SDK retried
    assert len(fake.specialist_attempts) == 1
    span = turn_span(result["trace_id"])
    assert span.attributes["support.turn.model_calls"] == 2  # triage + specialist: no model call was repeated
    assert span.attributes["support.turn.model_attempts"] == 4  # 3 + 1 HTTP attempts, retries included


def test_a_permanent_provider_error_is_not_retried() -> None:
    fake = FakeAnthropicHttp(lambda _attempt, _number: Fault(400))
    error = failed_with(one_turn(live_host(fake)), "internal_error")
    assert len(fake.attempts) == 1  # retrying a bad request can't help
    assert error["detail"] == resilience.INTERNAL.detail  # fixed text, never the provider's payload
    assert "fake failure" not in str(error)


def test_retries_are_bounded_to_exactly_the_configured_attempts_and_nothing_retries_above_the_sdk() -> None:
    fake = FakeAnthropicHttp(lambda _attempt, _number: Fault(500))
    result = one_turn(live_host(fake, model_max_retries=2))
    failed_with(result, "model_unavailable")
    assert len(fake.attempts) == 3  # 1 + 2 retries: no second layer multiplies them
    assert fake.specialist_attempts == []  # the workflow doesn't go on, and the turn isn't retried
    span = turn_span(result["trace_id"])
    assert span.attributes["support.turn.model_attempts"] == 3
    assert span.attributes["support.turn.error_code"] == "model_unavailable"


@pytest.mark.parametrize("retries", [0, 1, 4])
def test_the_configured_retry_count_is_the_one_the_sdk_uses(retries: int) -> None:
    fake = FakeAnthropicHttp(lambda _attempt, _number: Fault(503, {"retry-after-ms": "10"}))
    failed_with(one_turn(live_host(fake, model_max_retries=retries)), "model_unavailable")
    assert len(fake.attempts) == retries + 1


def test_an_overloaded_provider_is_reported_as_unavailable() -> None:
    fake = FakeAnthropicHttp(lambda _attempt, _number: Fault(529, {"retry-after-ms": "10"}))
    failed_with(one_turn(live_host(fake, model_max_retries=1)), "model_unavailable")
    assert len(fake.attempts) == 2


def test_a_connection_failure_is_retried_then_reported_as_unavailable() -> None:
    fake = FakeAnthropicHttp(lambda _attempt, _number: Fault(connection_error=True))
    failed_with(one_turn(live_host(fake, model_max_retries=1)), "model_unavailable")
    assert len(fake.attempts) == 2


def test_a_rate_limit_honours_the_providers_hint_and_reports_it() -> None:
    fake = FakeAnthropicHttp(lambda _attempt, _number: Fault(429, {"retry-after": "1"}))
    app = live_host(fake)
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        result = api.ask(conversation, ORDER_QUESTION)
        error = failed_with(result, "rate_limited")
        assert error["retry_after_seconds"] == 1
        # The stored turn keeps the hint: reading it back (or replaying it) gives the same error.
        stored = client.get(f"/api/v1/conversations/{conversation}/turns/{result['turn_id']}").json()
        assert stored["error"] == error
    assert len(fake.attempts) == 3
    gaps = [later.at - earlier.at for earlier, later in zip(fake.attempts, fake.attempts[1:], strict=False)]
    assert all(gap >= 0.9 for gap in gaps), gaps  # the SDK waited as the provider asked


def test_a_classified_failure_survives_a_restart_and_replays_unchanged(tmp_path: Path) -> None:
    storage = tmp_path / "support.db"
    fake = FakeAnthropicHttp(lambda _attempt, _number: Fault(429, {"retry-after-ms": "1500"}))
    turn_request = {"client_turn_id": str(uuid.uuid4()), "message": ORDER_QUESTION}
    with TestClient(live_host(fake, storage, model_max_retries=0)) as client:
        conversation = Api(client).create_conversation()
        first = client.post(f"/api/v1/conversations/{conversation}/turns", json=turn_request).json()
    error = failed_with(first, "rate_limited")
    assert error["retry_after_seconds"] == 2
    restarted = FakeAnthropicHttp()
    with TestClient(live_host(restarted, storage)) as client:
        stored = client.get(f"/api/v1/conversations/{conversation}/turns/{first['turn_id']}").json()
        replayed = client.post(f"/api/v1/conversations/{conversation}/turns", json=turn_request).json()
    assert stored["error"] == error
    assert replayed == stored  # the same client_turn_id replays the stored failure: no new attempt
    assert restarted.attempts == []


def test_a_provider_that_never_answers_times_out_per_attempt() -> None:
    fake = FakeAnthropicHttp(lambda _attempt, _number: Fault(delay=30))
    started = time.monotonic()
    failed_with(one_turn(live_host(fake, model_attempt_timeout_seconds=0.3, model_max_retries=1)), "timeout")
    assert len(fake.attempts) == 2
    assert time.monotonic() - started < 10


def test_the_live_client_is_built_with_the_configured_limits_and_closed_at_shutdown() -> None:
    fake = FakeAnthropicHttp()
    app = live_host(fake, model_max_retries=3, model_attempt_timeout_seconds=12)
    sdk: anthropic.AsyncAnthropic = app.state.agents.triage.client.anthropic_client
    assert sdk.max_retries == 3
    assert sdk.timeout == 12
    with TestClient(app):
        assert not fake.client.is_closed
    assert fake.client.is_closed


def test_a_failed_stream_ends_with_one_final_failed_event() -> None:
    fake = FakeAnthropicHttp(lambda _attempt, _number: Fault(500, {"retry-after-ms": "10"}))
    with TestClient(live_host(fake)) as client:
        conversation = Api(client).create_conversation()
        response = client.post(
            f"/api/v1/conversations/{conversation}/turns",
            json={"client_turn_id": str(uuid.uuid4()), "message": ORDER_QUESTION},
            headers=SSE,
        )
    final = final_of(parse_sse(response.text))
    failed_with(final, "model_unavailable")


# ---- Cancellation and shutdown during the SDK's backoff ---------------------------------------------------------------


class FirstAttempt:
    def __init__(self) -> None:
        self.seen = asyncio.Event()

    async def __call__(self, _attempt: Attempt) -> None:
        self.seen.set()


@pytest.mark.anyio
async def test_a_cancel_during_the_sdks_backoff_makes_no_further_attempt() -> None:
    fake = FakeAnthropicHttp(lambda _attempt, _number: Fault(500, {"retry-after": "5"}))
    first = FirstAttempt()
    fake.on_attempt = first
    app = live_host(fake)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
        conversation = (await client.post("/api/v1/conversations", json={})).json()["conversation_id"]
        started = time.monotonic()
        submitted = asyncio.create_task(
            client.post(
                f"/api/v1/conversations/{conversation}/turns",
                json={"client_turn_id": str(uuid.uuid4()), "message": ORDER_QUESTION},
            )
        )
        await asyncio.wait_for(first.seen.wait(), 5)
        await asyncio.sleep(0.05)  # the SDK is now waiting the 5 s the provider asked for
        turn_id = app.state.conversations._conversations[conversation].active_turn_id
        cancelled = await client.post(f"/api/v1/conversations/{conversation}/turns/{turn_id}/cancel")
        assert cancelled.status_code == 202
        result = (await submitted).json()
    assert_contract("turn-result", result)
    assert result["outcome"] == "cancelled"
    assert len(fake.attempts) == 1  # no new attempt after the cancel
    assert time.monotonic() - started < 3


@pytest.mark.anyio
async def test_shutdown_during_a_retry_stores_the_turn_and_closes_the_provider_client() -> None:
    fake = FakeAnthropicHttp(lambda _attempt, _number: Fault(500, {"retry-after": "5"}))
    first = FirstAttempt()
    fake.on_attempt = first
    app = live_host(fake)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
        async with app.router.lifespan_context(app):
            conversation = (await client.post("/api/v1/conversations", json={})).json()["conversation_id"]
            submitted = asyncio.create_task(
                client.post(
                    f"/api/v1/conversations/{conversation}/turns",
                    json={"client_turn_id": str(uuid.uuid4()), "message": ORDER_QUESTION},
                )
            )
            await asyncio.wait_for(first.seen.wait(), 5)
            await asyncio.sleep(0.05)
        # The lifespan ended: running turns were cancelled and stored before the clients closed.
        result = (await submitted).json()
    assert result["outcome"] == "cancelled"
    assert len(fake.attempts) == 1
    assert fake.client.is_closed


# ---- Turn budgets and tool limits (fake model) --------------------------------------------------------------------------


def looping(messages: Sequence[Message], options: Mapping[str, Any]) -> ChatResponse:
    """A model stuck in a loop: it asks for the same lookup again and again."""
    return tool_call("get_order_status", {"order_id": "ORD-1002"})


def test_a_model_that_loops_runs_out_of_budget_before_its_next_call_reaches_the_provider() -> None:
    app, _ = host(resilience=ResilienceSettings(max_model_calls_per_turn=3, max_tool_iterations=20))
    fake = model(app)
    fake.script = looping
    result = one_turn(app)
    error = failed_with(result, "budget_exhausted")
    assert error["detail"] == resilience.BUDGET.detail
    assert fake.calls == 3  # triage + 2 specialist calls; the 4th was refused before reaching the provider
    span = turn_span(result["trace_id"])
    assert span.attributes["support.turn.model_calls"] == 4  # the refused call is counted
    assert span.attributes["support.turn.error_code"] == "budget_exhausted"


def test_the_tool_loop_stops_at_its_limit_and_asks_for_a_final_answer() -> None:
    app, _ = host(resilience=ResilienceSettings(max_tool_iterations=2))
    fake = model(app)
    fake.script = looping
    result = one_turn(app)
    assert result["outcome"] == "completed"
    assert fake.calls == 1 + 2 + 1  # triage, two tool rounds, one final answer without tools


def test_a_tool_that_hangs_ends_the_turn_within_its_time_limit(monkeypatch: pytest.MonkeyPatch) -> None:
    def slow(self: OrderService, *_args: Any) -> Any:
        time.sleep(1.5)  # a sync tool runs in a worker thread: the turn stops waiting, the thread finishes harmlessly

    monkeypatch.setattr(OrderService, "order_status", slow)
    app, _ = host(resilience=ResilienceSettings(tool_timeout_seconds=0.2))
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        started = time.monotonic()
        result = api.ask(conversation, ORDER_QUESTION)
        assert time.monotonic() - started < 1.2  # the answer doesn't wait for the stuck lookup
    failed_with(result, "tool_unavailable")
    assert model(app).calls == 2  # triage + the call that asked for the tool; the model never got to retry it


def test_a_failing_tool_ends_the_turn_instead_of_letting_the_model_retry_it(monkeypatch: pytest.MonkeyPatch) -> None:
    def broken(self: OrderService, *_args: Any) -> Any:
        raise RuntimeError("the order database is down: internal detail")

    monkeypatch.setattr(OrderService, "order_status", broken)
    app, _ = host()
    result = one_turn(app)
    error = failed_with(result, "tool_unavailable")
    assert "internal detail" not in str(result)
    assert error["detail"] == resilience.TOOL.detail
    assert model(app).calls == 2


def test_an_argument_error_still_goes_back_to_the_model_and_is_not_a_tool_fault() -> None:
    app, _ = host()
    fake = model(app)
    seen: list[str] = []

    def bad_arguments_then_answer(messages: Sequence[Message], options: Mapping[str, Any]) -> ChatResponse:
        results = [c for c in messages[-1].contents if c.type == "function_result"]
        if results:
            seen.append(str(results[-1].result))
            return default_script(messages, options)
        return tool_call("search_knowledge_base", {"query": "x" * 1000})  # over the tool's 200-character limit

    fake.script = bad_arguments_then_answer
    result = one_turn(app, "My speaker crackles, how do I fix it?")
    assert result["outcome"] == "completed"
    assert seen  # the model saw the validation error and answered


# ---- The turn deadline -------------------------------------------------------------------------------------------------


async def never_answers() -> None:
    await asyncio.sleep(30)


def test_a_turn_past_its_deadline_fails_with_timeout() -> None:
    app, _ = host(resilience=ResilienceSettings(turn_timeout_seconds=0.3))
    model(app).before_call = never_answers
    started = time.monotonic()
    result = one_turn(app)
    failed_with(result, "timeout")
    assert time.monotonic() - started < 5
    assert turn_span(result["trace_id"]).attributes["support.turn.error_code"] == "timeout"


def ask_for_approval(app: FastAPI, client: TestClient, message: str) -> tuple[str, dict[str, Any]]:
    api = Api(client)
    conversation = api.create_conversation()
    asked = api.ask(conversation, message)
    assert asked["outcome"] == "awaiting_approval"
    return conversation, asked["pending_approval"]


@pytest.mark.parametrize("kind", ["cancel_order", "refund_order"])
def test_a_deadline_before_the_commit_fails_the_operation_and_changes_nothing(kind: str) -> None:
    message = "Please cancel order ORD-1003." if kind == "cancel_order" else "I want a refund for ORD-1001, it broke."
    app, _ = host(resilience=ResilienceSettings(turn_timeout_seconds=1))
    with TestClient(app) as client:
        conversation, pending = ask_for_approval(app, client, message)
        app.state.operations.before_commit = never_answers  # the write is held just before its transaction
        status, result = decide(client, conversation, pending, "approve")
        assert status == 200
        assert result["outcome"] == "completed"
        assert result["reply"]["kind"] == "approval_result"
        assert "Nothing has been changed" in result["reply"]["text"]
        operation = client.get(f"/api/v1/operations/{pending['operation_id']}").json()
        assert operation["status"] == "failed"
        assert operation["failure"]["code"] == "timeout"
        assert state_of(app, kind) == BEFORE[kind]
        # Retrying the decision replays the stored turn: the write never runs again.
        app.state.operations.before_commit = None
        status, again = decide(client, conversation, pending, "approve")
        assert status == 200
        assert again["turn_id"] == result["turn_id"]
        assert state_of(app, kind) == BEFORE[kind]


@pytest.mark.parametrize("kind", ["cancel_order", "refund_order"])
def test_a_deadline_racing_a_commit_reports_the_committed_write_and_never_repeats_it(kind: str) -> None:
    message = "Please cancel order ORD-1003." if kind == "cancel_order" else "I want a refund for ORD-1001, it broke."
    app, _ = host(resilience=ResilienceSettings(turn_timeout_seconds=1))
    with TestClient(app) as client:
        conversation, pending = ask_for_approval(app, client, message)
        # The write commits, then the model's reply never comes: the deadline hits after the commit.
        model(app).before_call = never_answers
        status, result = decide(client, conversation, pending, "approve")
        assert status == 200
        assert result["outcome"] == "completed"
        assert result["reply"]["text"].startswith("Done.")
        operation = client.get(f"/api/v1/operations/{pending['operation_id']}").json()
        assert operation["status"] == "committed"  # a timeout never rolls a write back
        assert state_of(app, kind) == AFTER[kind]
        model(app).before_call = None
        status, again = decide(client, conversation, pending, "approve")
        assert again["turn_id"] == result["turn_id"]
        assert state_of(app, kind) == AFTER[kind]  # applied once


def test_a_resumed_decision_is_bounded_by_the_turn_budget() -> None:
    app, _ = host(resilience=ResilienceSettings(max_model_calls_per_turn=2))
    with TestClient(app) as client:
        conversation, pending = ask_for_approval(app, client, "Please cancel order ORD-1003.")
        fake = model(app)
        fake.script = looping  # after the approved write, the model keeps calling tools
        status, result = decide(client, conversation, pending, "approve")
        assert status == 200
        assert result["outcome"] == "completed"
        # The write committed before the budget ran out; the turn reports what the record says.
        assert result["reply"]["text"].startswith("Done.")
        assert state_of(app, "cancel_order") == "cancelled"


# ---- Settings and classification ---------------------------------------------------------------------------------------


@pytest.mark.parametrize(
    "variable",
    [
        "RESILIENCE__TURN_TIMEOUT_SECONDS=0",
        "RESILIENCE__TURN_TIMEOUT_SECONDS=-5",
        "RESILIENCE__MAX_MODEL_CALLS_PER_TURN=0",
        "RESILIENCE__MAX_TOOL_ITERATIONS=0",
        "RESILIENCE__TOOL_TIMEOUT_SECONDS=100000",
        "RESILIENCE__MODEL_MAX_RETRIES=-1",
        "RESILIENCE__MODEL_MAX_RETRIES=11",
        "RESILIENCE__MODEL_ATTEMPT_TIMEOUT_SECONDS=0",
        "RESILIENCE__MAX_OUTPUT_TOKENS=0",
    ],
)
def test_limits_that_would_break_or_unbound_a_turn_refuse_to_start(
    monkeypatch: pytest.MonkeyPatch, variable: str
) -> None:
    name, value = variable.split("=")
    monkeypatch.setenv(name, value)
    with pytest.raises(ValidationError):
        Settings()


def test_limits_are_read_from_the_environment(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("RESILIENCE__TURN_TIMEOUT_SECONDS", "45")
    monkeypatch.setenv("RESILIENCE__MODEL_MAX_RETRIES", "0")
    limits = Settings().resilience
    assert (limits.turn_timeout_seconds, limits.model_max_retries) == (45, 0)
    assert ResilienceSettings() == ResilienceSettings(
        turn_timeout_seconds=90,
        max_model_calls_per_turn=10,
        max_tool_iterations=4,
        tool_timeout_seconds=10,
        model_max_retries=2,
        model_attempt_timeout_seconds=30,
        max_output_tokens=1024,
    )  # the same defaults as .NET's L15


def test_every_agent_is_bounded_the_same_way() -> None:
    app, _ = host(resilience=ResilienceSettings(max_tool_iterations=3, max_output_tokens=500))
    for agent in app.state.agents.all():
        middleware: list[Any] = list(agent.middleware or [])
        kinds: set[type] = {type(each) for each in middleware}
        assert {resilience.ModelCallBudget, resilience.ToolLimits} <= kinds, agent.name
        assert agent.default_options["max_tokens"] == 500
    configuration: dict[str, Any] = app.state.agents.triage.client.function_invocation_configuration
    assert configuration["max_iterations"] == 3


def status_error(status: int, headers: dict[str, str] | None = None) -> anthropic.APIStatusError:
    request = httpx2.Request("POST", "https://api.anthropic.com/v1/messages")
    response = httpx2.Response(status, headers=headers or {}, request=request)
    classes: dict[int, type[anthropic.APIStatusError]] = {
        400: anthropic.BadRequestError,
        401: anthropic.AuthenticationError,
        429: anthropic.RateLimitError,
        500: anthropic.InternalServerError,
        529: anthropic.OverloadedError,
    }
    return classes.get(status, anthropic.APIStatusError)("failure", response=response, body=None)


@pytest.mark.parametrize(
    ("error", "code"),
    [
        (status_error(400), "internal_error"),
        (status_error(401), "internal_error"),
        (status_error(422), "internal_error"),
        (status_error(500), "model_unavailable"),
        (status_error(529), "model_unavailable"),
        (status_error(429), "rate_limited"),
        (anthropic.APITimeoutError(httpx2.Request("POST", "https://x")), "timeout"),
        (anthropic.APIConnectionError(request=httpx2.Request("POST", "https://x")), "model_unavailable"),
        (resilience.BudgetExhaustedError("x"), "budget_exhausted"),
        (resilience.ToolUnavailableError("x"), "tool_unavailable"),
        (ValueError("anything else"), "internal_error"),
    ],
)
def test_failures_are_classified_through_the_frameworks_wrapping(error: Exception, code: str) -> None:
    # MAF wraps provider errors (ChatClientException with inner_exception), and the workflow may chain them again.
    wrapped = ChatClientException("Anthropic chat request failed", inner_exception=error)
    outer = RuntimeError("The workflow failed.")
    outer.__cause__ = wrapped
    assert resilience.classify(outer).code == code
    assert resilience.classify(error).code == code


@pytest.mark.parametrize(
    ("headers", "seconds"),
    [
        ({"retry-after": "7"}, 7),
        ({"retry-after": "0.2"}, 1),
        ({"retry-after-ms": "2500"}, 3),
        ({"retry-after-ms": "2500", "retry-after": "9"}, 3),
        ({"retry-after": "not a number or a date"}, None),
        ({}, None),
    ],
)
def test_the_retry_hint_is_read_from_the_provider_response(headers: dict[str, str], seconds: int | None) -> None:
    assert resilience.classify(status_error(429, headers)).retry_after_seconds == seconds


def test_a_turn_timeout_wins_over_any_other_classification() -> None:
    assert resilience.classify(status_error(500), turn_timed_out=True) == resilience.TIMEOUT
