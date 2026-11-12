"""Hermetic test settings: fake mode, development identity, and nothing read from the developer's environment."""

from __future__ import annotations

import os
import tempfile

import pytest

# Importing the app module builds a default host: point it at a throwaway file, never the developer's local database.
os.environ["STORAGE__PATH"] = os.path.join(tempfile.mkdtemp(prefix="support-import-"), "support.db")

# Telemetry: providers are once per process, so the whole test run shares in-memory exporters, installed before
# any app is built. Nothing is exported over OTLP from tests, even inside an Aspire-launched shell.
for _name in [name for name in os.environ if name.upper().startswith("OTEL_")]:
    del os.environ[_name]

from opentelemetry.sdk._logs.export import InMemoryLogRecordExporter  # noqa: E402
from opentelemetry.sdk.metrics.export import InMemoryMetricReader  # noqa: E402
from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter  # noqa: E402

from support_agent import telemetry  # noqa: E402
from support_agent.settings import Environment, IdentitySettings, Settings  # noqa: E402

SPANS = InMemorySpanExporter()
METRICS = InMemoryMetricReader()
LOGS = InMemoryLogRecordExporter()
telemetry.configure(
    Settings(app_environment=Environment.DEVELOPMENT, identity=IdentitySettings(development_customer_id="CUST-1001")),
    span_exporters=[SPANS],
    metric_readers=[METRICS],
    log_exporters=[LOGS],
)

ENVIRONMENT_PREFIXES = ("MODEL_PROVIDER__", "IDENTITY__", "APP_ENVIRONMENT", "ANTHROPIC_API_KEY")


@pytest.fixture(autouse=True)
def clean_environment(monkeypatch: pytest.MonkeyPatch, tmp_path: os.PathLike[str]) -> None:
    for name in list(os.environ):
        if name.upper().startswith(ENVIRONMENT_PREFIXES):
            monkeypatch.delenv(name, raising=False)
    # Every test gets its own database file, also when it builds Settings itself (live and host tests).
    monkeypatch.setenv("STORAGE__PATH", os.path.join(tmp_path, "support.db"))
