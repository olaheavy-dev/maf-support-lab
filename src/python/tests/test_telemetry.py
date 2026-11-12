"""The Python backend's own telemetry, read from the in-memory exporters installed by the conftest.

Spans are found by trace id (other tests run in the same process). Checked: one connected trace per turn, a distinct
service identity, real metric samples with bounded labels, logs on the turn's trace, no message content or customer
ids by default, nothing duplicated (including by DevUI), usage the provider didn't report recorded as nothing, and
configuration errors that fail at startup instead of exporting nowhere.
"""

from __future__ import annotations

import json
import re
import subprocess
import sys
import time
import uuid
from collections.abc import Iterator
from typing import Any

import pytest
from fastapi.testclient import TestClient
from opentelemetry import trace
from opentelemetry.sdk.trace import ReadableSpan
from opentelemetry.trace import SpanKind, StatusCode

from support_agent import telemetry
from support_agent.fake_model import FAILURE_TRIGGER, INPUT_TOKENS, ScriptedChatClient
from tests.conftest import LOGS, METRICS, SPANS
from tests.helpers import SSE, Api, final_of, host, parse_sse

IDS = re.compile(r"\b(conv|turn|op|appr)_[0-9a-f]{16}\b|\b[0-9a-f]{32}\b")


def spans_of(trace_id: str) -> list[ReadableSpan]:
    telemetry.configured().flush()  # type: ignore[union-attr]
    return [
        s
        for s in SPANS.get_finished_spans()
        if s.context is not None and format(s.context.trace_id, "032x") == trace_id
    ]


def names(spans: list[ReadableSpan]) -> list[str]:
    return [s.name for s in spans]


def one(spans: list[ReadableSpan], name: str) -> ReadableSpan:
    found = [s for s in spans if s.name == name]
    assert len(found) == 1, f"{name}: {len(found)} in {names(spans)}"
    return found[0]


def children(spans: list[ReadableSpan], parent: ReadableSpan) -> list[ReadableSpan]:
    assert parent.context is not None
    return [s for s in spans if s.parent is not None and s.parent.span_id == parent.context.span_id]


def ancestors(spans: list[ReadableSpan], span: ReadableSpan) -> Iterator[ReadableSpan]:
    by_id = {s.context.span_id: s for s in spans if s.context is not None}
    while span.parent is not None and span.parent.span_id in by_id:
        span = by_id[span.parent.span_id]
        yield span


def span_processors(provider: Any) -> list[Any]:
    """The provider's span processors (an SDK internal: the only way to see whether anything added an exporter)."""
    return list(provider._active_span_processor._span_processors)


def points(metric_name: str) -> list[Any]:
    """Every data point of one metric, across scopes (cumulative since the process started)."""
    found: list[Any] = []
    data = METRICS.get_metrics_data()
    if data is None:
        return found
    for resource_metrics in data.resource_metrics:
        for scope_metrics in resource_metrics.scope_metrics:
            for metric in scope_metrics.metrics:
                if metric.name == metric_name:
                    found.extend(metric.data.data_points)
    return found


def total(metric_name: str, **labels: str) -> float:
    selected = [p for p in points(metric_name) if all(p.attributes.get(k) == v for k, v in labels.items())]
    values: list[float] = [float(p.value) if hasattr(p, "value") else float(p.count) for p in selected]
    return sum(values)


def token_sum(token_type: str) -> float:
    return sum(
        p.sum for p in points("gen_ai.client.token.usage") if p.attributes.get("gen_ai.token.type") == token_type
    )


def ask(message: str) -> tuple[Any, dict[str, Any]]:
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        _, turn = api.send(conversation, message)
    return app, turn


def test_one_turn_is_one_connected_trace_from_http_to_tools() -> None:
    _, turn = ask("Where is my order ORD-1002?")
    spans = spans_of(turn["trace_id"])  # the contract's trace_id is the trace's own id

    server = [s for s in spans if s.kind is SpanKind.SERVER]
    assert len(server) == 1  # one HTTP server span: FastAPI's built-in telemetry, nothing duplicating it
    assert server[0].name == "POST /api/v1/conversations/{conversation_id}/turns"
    turn_span = one(spans, "support.turn")
    assert server[0] in list(ancestors(spans, turn_span))  # the detached task kept the request's trace
    for name in (
        "invoke_workflow support-routing",
        "executor.process triage",
        "executor.process orders-specialist",
        "invoke_agent triage",
        "invoke_agent orders",
        "execute_tool get_order_status",
        "executor.process reply",
    ):
        assert turn_span in list(ancestors(spans, one(spans, name))), name
    assert len([s for s in spans if s.name.startswith("chat ")]) == 3  # triage, and the specialist's two calls
    assert turn_span.attributes is not None
    assert turn_span.attributes["support.turn.outcome"] == "completed"
    assert turn_span.attributes["support.agent"] == "orders"
    assert turn_span.attributes["support.reply_kind"] == "answer"
    assert turn_span.attributes["support.turn.id"] == turn["turn_id"]
    assert turn_span.resource.attributes["service.name"] == "support-api-python"
    assert turn_span.resource.attributes["deployment.environment.name"] == "development"
    assert turn_span.resource.attributes["service.version"]


def test_a_failed_turn_marks_its_trace_and_logs_the_failure_on_it() -> None:
    _, turn = ask(f"Where is my order ORD-1002? {FAILURE_TRIGGER}")
    assert turn["outcome"] == "failed"
    spans = spans_of(turn["trace_id"])
    turn_span = one(spans, "support.turn")
    assert turn_span.status.status_code is StatusCode.ERROR
    assert turn_span.attributes is not None
    assert turn_span.attributes["error.type"] == "internal_error"
    assert turn_span.attributes["support.turn.outcome"] == "failed"

    trace_id = int(turn["trace_id"], 16)
    logged = [r.log_record for r in LOGS.get_finished_logs() if r.log_record.trace_id == trace_id]
    failed = [r for r in logged if r.body == "Turn failed"]
    assert len(failed) == 1
    assert failed[0].attributes is not None
    assert failed[0].attributes["support.turn.id"] == turn["turn_id"]
    assert failed[0].severity_text == "ERROR"
    assert any(r.exception is not None or "Turn " in str(r.body) for r in logged)  # the traceback travels with it


def test_metrics_have_real_samples_with_bounded_labels() -> None:
    before = {
        "turns": total("support.turns", **{"support.agent": "orders", "support.turn.outcome": "completed"}),
        "tools": total(
            "support.tool.calls", **{"support.tool.name": "get_order_status", "support.tool.outcome": "found"}
        ),
        "triage": total("support.triage.decisions", **{"support.triage.target": "orders"}),
        "proposed": total("support.operations", **{"support.operation.outcome": "awaiting_approval"}),
        "committed": total("support.operations", **{"support.operation.outcome": "committed"}),
    }
    app, _ = host()
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        api.ask(conversation, "Where is my order ORD-1002?")
        pending = api.ask(conversation, "Please cancel order ORD-1003.")["pending_approval"]
        client.post(
            f"/api/v1/conversations/{conversation}/approvals/{pending['approval_id']}",
            json={"operation_id": pending["operation_id"], "decision": "approve"},
        )

    assert (
        total("support.turns", **{"support.agent": "orders", "support.turn.outcome": "completed"})
        >= before["turns"] + 2
    )
    assert (
        total("support.tool.calls", **{"support.tool.name": "get_order_status", "support.tool.outcome": "found"})
        > before["tools"]
    )
    assert total("support.triage.decisions", **{"support.triage.target": "orders"}) >= before["triage"] + 2
    assert total("support.operations", **{"support.operation.outcome": "awaiting_approval"}) == before["proposed"] + 1
    assert total("support.operations", **{"support.operation.outcome": "committed"}) == before["committed"] + 1
    assert points("support.turn.duration")
    assert points("http.server.request.duration")
    assert points("gen_ai.client.operation.duration")

    # No id is ever a label: every label value comes from a small fixed set.
    data = METRICS.get_metrics_data()
    assert data is not None
    for resource_metrics in data.resource_metrics:
        for scope_metrics in resource_metrics.scope_metrics:
            for metric in scope_metrics.metrics:
                for point in metric.data.data_points:
                    for key, value in (point.attributes or {}).items():
                        assert not IDS.search(str(value)), f"{metric.name}: {key}={value}"


def test_token_usage_is_counted_once_per_model_call() -> None:
    # .NET counted tokens twice (agent and model layers) and needed a View; MAF Python records them once per call.
    before = token_sum("input")
    _, turn = ask("Where is my order ORD-1002?")
    calls = len([s for s in spans_of(turn["trace_id"]) if s.name.startswith("chat ")])
    assert calls == 3
    assert token_sum("input") - before == calls * INPUT_TOKENS


def test_usage_the_provider_did_not_report_is_recorded_as_nothing_not_zero() -> None:
    app, _ = host()
    model: ScriptedChatClient = app.state.agents.triage.client
    model.report_usage = False
    before = sum(p.count for p in points("gen_ai.client.token.usage"))
    with TestClient(app) as client:
        api = Api(client)
        conversation = api.create_conversation()
        _, turn = api.send(conversation, "Where is my order ORD-1002?")
    assert turn["outcome"] == "completed"
    assert sum(p.count for p in points("gen_ai.client.token.usage")) == before  # no samples, not zero-valued ones
    chats = [s for s in spans_of(turn["trace_id"]) if s.name.startswith("chat ")]
    assert chats
    assert all("gen_ai.usage.input_tokens" not in (s.attributes or {}) for s in chats)


def test_no_message_text_tool_data_or_customer_id_is_captured_by_default() -> None:
    secret = "my fridge hums at 3am"
    _, turn = ask(f"Where is my order ORD-1002? {secret}")
    spans = spans_of(turn["trace_id"])
    trace_id = int(turn["trace_id"], 16)
    logs = [r.log_record for r in LOGS.get_finished_logs() if r.log_record.trace_id == trace_id]
    dumped = json.dumps(
        [
            {
                "name": s.name,
                "attributes": dict(s.attributes or {}),
                "events": [dict(e.attributes or {}) for e in s.events],
            }
            for s in spans
        ]
        + [{"body": str(r.body), "attributes": dict(r.attributes or {})} for r in logs],
        default=str,
    )
    assert secret not in dumped
    assert "CUST-1001" not in dumped
    assert "Smart Speaker" not in dumped  # the tool's result (the order's item)


def test_health_probes_are_not_traced() -> None:
    app, _ = host()
    with TestClient(app) as client:
        since = time.time_ns()
        client.get("/health")
        client.get("/alive")
    telemetry.configured().flush()  # type: ignore[union-attr]
    after = [s for s in SPANS.get_finished_spans() if s.start_time and s.start_time >= since]
    assert not [s for s in after if "/health" in s.name or "/alive" in s.name]


def test_streaming_records_the_time_to_the_first_reply_text() -> None:
    before = sum(p.count for p in points("support.stream.first_delta"))
    app, _ = host()
    with TestClient(app) as client:
        conversation = Api(client).create_conversation()
        response = client.post(
            f"/api/v1/conversations/{conversation}/turns",
            json={"client_turn_id": str(uuid.uuid4()), "message": "Where is my order ORD-1002?"},
            headers=SSE,
        )
    assert final_of(parse_sse(response.text))["outcome"] == "completed"
    assert sum(p.count for p in points("support.stream.first_delta")) == before + 1


def test_providers_are_set_up_once_and_devui_adds_nothing() -> None:
    from agent_framework.observability import OBSERVABILITY_SETTINGS
    from agent_framework_devui import DevServer

    from support_agent.devui import entities
    from support_agent.identity import set_process_principal
    from support_agent.settings import Environment, IdentitySettings, Settings

    configured = telemetry.configured()
    assert configured is not None
    assert telemetry.configure(Settings()) is configured  # a second host reuses the process's providers
    assert trace.get_tracer_provider() is configured.tracer_provider
    assert OBSERVABILITY_SETTINGS._executed_setup  # pyright: ignore[reportPrivateUsage]
    processors = list(span_processors(configured.tracer_provider))
    handlers = list(__import__("logging").getLogger().handlers)

    server = DevServer(host="127.0.0.1", auth_enabled=False, ui_enabled=False)
    server.register_entities(
        entities(
            Settings(
                app_environment=Environment.DEVELOPMENT, identity=IdentitySettings(development_customer_id="CUST-1001")
            )
        )
    )
    try:
        with TestClient(server.get_app(), base_url="http://127.0.0.1") as client:
            assert client.get("/v1/entities").status_code == 200  # DevUI's executor (and its telemetry setup) ran
    finally:
        set_process_principal(None)

    assert trace.get_tracer_provider() is configured.tracer_provider
    assert list(span_processors(configured.tracer_provider)) == processors
    assert list(__import__("logging").getLogger().handlers) == handlers
    assert not OBSERVABILITY_SETTINGS.SENSITIVE_DATA_ENABLED


def run_isolated(code: str, **env: str) -> subprocess.CompletedProcess[str]:
    """Telemetry is once per process, so configuration edge cases run in a fresh interpreter."""
    import os

    clean = {k: v for k, v in os.environ.items() if not k.upper().startswith(("OTEL_", "ENABLE_"))}
    return subprocess.run(  # noqa: S603 - our own fixed snippets, run with this interpreter
        [sys.executable, "-c", code], env={**clean, **env}, capture_output=True, text=True, timeout=60
    )


SETUP = """
from support_agent import telemetry
from support_agent.settings import Settings
t = telemetry.configure(Settings())
"""


def test_a_protocol_this_backend_cannot_speak_fails_at_startup() -> None:
    result = run_isolated(
        SETUP, OTEL_EXPORTER_OTLP_ENDPOINT="http://127.0.0.1:4318", OTEL_EXPORTER_OTLP_PROTOCOL="http/protobuf"
    )
    assert result.returncode != 0
    assert "TelemetryConfigurationError" in result.stderr
    assert "OTEL_EXPORTER_OTLP_PROTOCOL is 'http/protobuf'" in result.stderr


def test_an_endpoint_switches_on_the_otlp_exporters_and_none_means_no_export() -> None:
    probe = (
        SETUP
        + """
from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter
processors = t.tracer_provider._active_span_processor._span_processors
print(t.exporting, any(isinstance(getattr(p, "span_exporter", None), OTLPSpanExporter) for p in processors), t.service_name)
"""
    )
    exporting = run_isolated(
        probe, OTEL_EXPORTER_OTLP_ENDPOINT="https://localhost:21000", OTEL_SERVICE_NAME="support-api-python"
    )
    assert exporting.stdout.split() == ["True", "True", "support-api-python"], exporting.stderr
    standalone = run_isolated(probe)
    assert standalone.stdout.split() == ["False", "False", "support-api-python"], standalone.stderr


def test_sensitive_capture_stays_off_even_if_the_framework_variable_says_on() -> None:
    probe = (
        SETUP
        + """
from agent_framework.observability import OBSERVABILITY_SETTINGS
print(OBSERVABILITY_SETTINGS.SENSITIVE_DATA_ENABLED)
"""
    )
    assert run_isolated(probe, ENABLE_SENSITIVE_DATA="true").stdout.strip() == "False"
    opted_in = run_isolated(probe, ENABLE_SENSITIVE_DATA="false", TELEMETRY__CAPTURE_SENSITIVE_DATA="true")
    assert opted_in.stdout.strip() == "True", opted_in.stderr


def test_shutdown_flushes_the_hosts_telemetry() -> None:
    app, _ = host()
    with TestClient(app):
        pass
    assert "telemetry" in app.state.resources.closed


@pytest.mark.parametrize("service", ["support-api-python", "support-devui-python"])
def test_each_python_process_has_its_own_service_name(service: str) -> None:
    default = "support-devui-python" if service.endswith("devui-python") else "support-api-python"
    probe = f"""
from support_agent import telemetry
from support_agent.settings import Settings
print(telemetry.configure(Settings(), default_service_name={default!r}).service_name)
"""
    assert run_isolated(probe).stdout.strip() == service


def test_an_unreachable_collector_never_holds_the_process_on_exit() -> None:
    # Found while building it: the gRPC exporters retry, and the SDK's own exit hooks have no limit (over a minute). Ours does.
    import time

    started = time.monotonic()
    pending = """
import logging
with telemetry.tracer.start_as_current_span("pending"):
    logging.getLogger("support_agent").info("pending")
telemetry.turns.add(1, {"support.agent": "orders"})
print(t.exporting)
"""  # a span, a log record, and a metric waiting in every provider
    result = run_isolated(SETUP + pending, OTEL_EXPORTER_OTLP_ENDPOINT="https://localhost:21000")
    assert result.stdout.strip() == "True", result.stderr
    assert time.monotonic() - started < telemetry.SHUTDOWN_TIMEOUT_SECONDS + 4
