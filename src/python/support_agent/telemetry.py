"""Python-owned OpenTelemetry (L40): one set of providers per process, exported to Aspire over OTLP/gRPC.

Who owns what:
- **This module** builds the trace, metric, and log providers once per process and installs them globally. It reads
  the endpoint, headers, certificate, and export interval from the standard ``OTEL_*`` variables that the Aspire
  AppHost sets. With no endpoint (a standalone run, or tests), nothing is exported.
- **FastAPI** (0.162, built in) creates the HTTP server spans and ``http.server.*`` metrics on these providers.
- **Agent Framework** emits the workflow, executor, ``invoke_agent``, ``chat``, and ``execute_tool`` spans and the
  ``gen_ai.*`` metrics. Its instrumentation is turned on here, with message capture explicitly off unless configured.
- **Our code** adds the ``support.turn`` span, the ``support.*`` metrics, and structured logs (``support_agent``
  loggers), through the instruments below.

Nothing else creates providers or exporters. DevUI would (its executor calls MAF's ``configure_otel_providers``), so
this module marks MAF's one-time setup as done; ``tests/test_telemetry.py`` checks that nothing is duplicated.
Message text, tool arguments and results, and customer ids never appear in telemetry unless capture is opted into;
conversation and turn ids appear on spans and logs, never as metric labels.
"""

from __future__ import annotations

import atexit
import importlib.metadata
import logging
import os
import threading
from collections.abc import Sequence
from dataclasses import dataclass, field

from agent_framework.observability import OBSERVABILITY_SETTINGS, enable_instrumentation
from opentelemetry import metrics, trace
from opentelemetry._logs import SeverityNumber, set_logger_provider
from opentelemetry.context import get_current
from opentelemetry.sdk._logs import LoggerProvider
from opentelemetry.sdk._logs.export import BatchLogRecordProcessor, LogRecordExporter, SimpleLogRecordProcessor
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import MetricReader, PeriodicExportingMetricReader
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor, SimpleSpanProcessor, SpanExporter

from support_agent.settings import Settings

SCOPE = "support_agent"  # the tracer, meter, and logger name for our own signals
DEFAULT_SERVICE_NAME = "support-api-python"
SUPPORTED_PROTOCOL = "grpc"  # the owner's choice (L40); the AppHost requests it explicitly
FLUSH_TIMEOUT_MILLIS = 2000
SHUTDOWN_TIMEOUT_SECONDS = 5.0

log = logging.getLogger(__name__)


class TelemetryConfigurationError(ValueError):
    """Telemetry settings that would silently export nothing (for example, a protocol this backend can't speak)."""


@dataclass
class Telemetry:
    tracer_provider: TracerProvider
    meter_provider: MeterProvider
    logger_provider: LoggerProvider
    exporting: bool
    service_name: str
    capture_sensitive_data: bool
    handlers: list[logging.Handler] = field(default_factory=list[logging.Handler])

    def flush(self, timeout_millis: int = FLUSH_TIMEOUT_MILLIS) -> None:
        """Pushes everything recorded so far to the exporters (at shutdown, and in tests), within a time limit."""
        self.tracer_provider.force_flush(timeout_millis)
        self.meter_provider.force_flush(timeout_millis)
        self.logger_provider.force_flush(timeout_millis)

    def shutdown(self, timeout_seconds: float = SHUTDOWN_TIMEOUT_SECONDS) -> None:
        """Flushes and closes the providers at process exit, never for longer than ``timeout_seconds``.

        With an unreachable collector the gRPC exporters keep retrying; without a limit the process hung on exit for
        over a minute (found by L40's tests). Telemetry not sent by then is dropped, and the process exits."""

        def close() -> None:
            self.flush()
            self.tracer_provider.shutdown()
            self.meter_provider.shutdown()
            self.logger_provider.shutdown()

        worker = threading.Thread(target=close, name="telemetry-shutdown", daemon=True)
        worker.start()
        worker.join(timeout_seconds)


_configured: Telemetry | None = None

# Attributes every logging.LogRecord has; anything else came from ``extra=`` and becomes a log attribute.
_STANDARD = set(vars(logging.LogRecord("", 0, "", 0, "", None, None))) | {"message", "asctime", "taskName"}
_SEVERITY = {
    logging.DEBUG: SeverityNumber.DEBUG,
    logging.INFO: SeverityNumber.INFO,
    logging.WARNING: SeverityNumber.WARN,
    logging.ERROR: SeverityNumber.ERROR,
    logging.CRITICAL: SeverityNumber.FATAL,
}


class TraceLogHandler(logging.Handler):
    """Sends Python log records to the OpenTelemetry logs pipeline, stamped with the active trace and span.

    Our own small handler on the stable logs API: the SDK's ``LoggingHandler`` is deprecated in 1.45, and its
    replacement lives in a prerelease contrib package. Attributes come only from ``extra=`` (our code passes ids and
    outcomes there, never message text)."""

    def __init__(self, logger_provider: LoggerProvider, level: int = logging.NOTSET) -> None:
        super().__init__(level)
        self._provider = logger_provider

    def emit(self, record: logging.LogRecord) -> None:
        try:
            attributes = {
                key: value
                for key, value in vars(record).items()
                if key not in _STANDARD and isinstance(value, str | bool | int | float)
            }
            self._provider.get_logger(record.name).emit(
                timestamp=int(record.created * 1e9),
                context=get_current(),
                severity_number=_SEVERITY.get(record.levelno, SeverityNumber.INFO),
                severity_text=record.levelname,
                body=record.getMessage(),
                attributes=attributes,
                exception=record.exc_info[1] if record.exc_info else None,
            )
        except Exception:
            self.handleError(record)


def configured() -> Telemetry | None:
    return _configured


def otlp_endpoint() -> str | None:
    endpoint = os.environ.get("OTEL_EXPORTER_OTLP_ENDPOINT", "").strip()
    return endpoint or None


def configure(
    settings: Settings,
    *,
    default_service_name: str = DEFAULT_SERVICE_NAME,
    span_exporters: Sequence[SpanExporter] = (),
    metric_readers: Sequence[MetricReader] = (),
    log_exporters: Sequence[LogRecordExporter] = (),
) -> Telemetry:
    """Sets up the process's providers once; later calls return the same ones (tests build many hosts).

    ``span_exporters``, ``metric_readers``, and ``log_exporters`` are added beside OTLP; tests pass in-memory ones."""
    global _configured
    if _configured is not None:
        return _configured

    endpoint = otlp_endpoint()
    protocol = os.environ.get("OTEL_EXPORTER_OTLP_PROTOCOL", SUPPORTED_PROTOCOL).strip().lower()
    if endpoint is not None and protocol != SUPPORTED_PROTOCOL:
        # Fail at startup, not by exporting into the void: a gRPC exporter can't talk to an HTTP/protobuf endpoint.
        raise TelemetryConfigurationError(
            f"OTEL_EXPORTER_OTLP_PROTOCOL is '{protocol}', but this backend exports OTLP over gRPC. "
            "Point it at the collector's gRPC endpoint with OTEL_EXPORTER_OTLP_PROTOCOL=grpc."
        )

    service_name = os.environ.get("OTEL_SERVICE_NAME", "").strip() or default_service_name
    # Resource.create also reads OTEL_RESOURCE_ATTRIBUTES (Aspire puts service.instance.id there).
    resource = Resource.create(
        {
            "service.name": service_name,
            "service.version": importlib.metadata.version("support-agent"),
            "deployment.environment.name": str(settings.app_environment),
        }
    )

    # shutdown_on_exit=False: the SDK's own exit hooks have no time limit; ours (Telemetry.shutdown) does.
    tracer_provider = TracerProvider(resource=resource, shutdown_on_exit=False)
    readers: list[MetricReader] = list(metric_readers)
    logger_provider = LoggerProvider(resource=resource, shutdown_on_exit=False)
    for exporter in span_exporters:
        tracer_provider.add_span_processor(SimpleSpanProcessor(exporter))
    for exporter in log_exporters:
        logger_provider.add_log_record_processor(SimpleLogRecordProcessor(exporter))
    if endpoint is not None:
        # gRPC exporters read the endpoint, headers (the dashboard's API key), certificate, and timeout from OTEL_*.
        from opentelemetry.exporter.otlp.proto.grpc._log_exporter import OTLPLogExporter
        from opentelemetry.exporter.otlp.proto.grpc.metric_exporter import OTLPMetricExporter
        from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter

        tracer_provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter()))
        readers.append(PeriodicExportingMetricReader(OTLPMetricExporter()))  # interval: OTEL_METRIC_EXPORT_INTERVAL
        logger_provider.add_log_record_processor(BatchLogRecordProcessor(OTLPLogExporter()))
    meter_provider = MeterProvider(resource=resource, metric_readers=readers, shutdown_on_exit=False)

    trace.set_tracer_provider(tracer_provider)
    metrics.set_meter_provider(meter_provider)
    set_logger_provider(logger_provider)

    # One handler, on the root logger: our logs and the framework's carry the active trace and span ids.
    handler = TraceLogHandler(logger_provider, level=logging.INFO)
    logging.getLogger().addHandler(handler)
    logging.getLogger(SCOPE).setLevel(logging.INFO)

    # Agent Framework's instrumentation, with message capture set explicitly: ENABLE_SENSITIVE_DATA in the environment
    # can't switch it on by accident.
    capture = settings.telemetry.capture_sensitive_data
    enable_instrumentation(enable_sensitive_data=capture, force=True)
    # MAF's own one-time provider setup must not run on top of ours (DevUI calls it): mark it as done.
    OBSERVABILITY_SETTINGS._executed_setup = True  # pyright: ignore[reportPrivateUsage]

    _configured = Telemetry(
        tracer_provider, meter_provider, logger_provider, endpoint is not None, service_name, capture, [handler]
    )
    atexit.register(_configured.shutdown)
    log.info(
        "Telemetry configured",
        extra={"telemetry.exporting": endpoint is not None, "telemetry.capture_sensitive_data": capture},
    )
    return _configured


tracer = trace.get_tracer(SCOPE)
meter = metrics.get_meter(SCOPE)

# Our metrics. Every label is from a small fixed set; ids are never labels (each value would be a new time series).
turns = meter.create_counter(
    "support.turns", unit="{turn}", description="Finished turns, by agent, outcome, reply kind."
)
turn_duration = meter.create_histogram(
    "support.turn.duration", unit="s", description="Turn duration, by agent and outcome."
)
tool_calls = meter.create_counter(
    "support.tool.calls", unit="{call}", description="Read-tool calls, by tool and outcome."
)
operations = meter.create_counter(
    "support.operations", unit="{operation}", description="Write operations, by kind and outcome."
)
triage_decisions = meter.create_counter(
    "support.triage.decisions", unit="{decision}", description="Triage decisions, by outcome, target, reason."
)
first_delta = meter.create_histogram(
    "support.stream.first_delta", unit="s", description="Time from turn start to the first streamed reply text (L20)."
)


def current_trace_id() -> str | None:
    """The active trace's id (32 hex), the one the contract's ``trace_id`` reports; None outside any trace."""
    context = trace.get_current_span().get_span_context()
    return format(context.trace_id, "032x") if context.is_valid else None
