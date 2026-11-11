"""Cross-backend contract probes (L21): the same requests against the real .NET and Python servers, compared.

Each backend runs as real processes in fake model mode (no key, no network): one as CUST-1001 and one as CUST-1002,
on the same SQLite file, so ownership is checked by the server that reads the other customer's stored record. Every
response is validated against the contract schemas, then normalized (ids, timestamps, and model-written wording replaced
by placeholders) and compared between the two backends. Any difference fails, except the ones listed in
``ALLOWED_DIFFERENCES``, each of which is a documented implementation detail, never a business or security rule.

This is a focused feature/contract check, not the evaluation harness (E08): it asserts observable contract behaviour
on the shared synthetic fixtures, not answer quality.

    python contracts/probes/run_probes.py            # build nothing, start both, run, compare
    python contracts/probes/run_probes.py --dotnet-build   # build the .NET API first (Release)

It imports neither backend. It needs the contract test tools (contracts/requirements.txt) and both toolchains.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from collections.abc import Callable, Iterator
from contextlib import contextmanager
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "contracts" / "tests"))

from schema_registry import validator  # noqa: E402

OWNER, OTHER = "CUST-1001", "CUST-1002"
STARTUP_SECONDS = 120
REQUEST_SECONDS = 30


# ---- Processes ----------------------------------------------------------------------------------------------------------


def free_port() -> int:
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return int(probe.getsockname()[1])


def child_environment(extra: dict[str, str]) -> dict[str, str]:
    """No telemetry export, no provider key, no inherited model mode: the probes are hermetic."""
    blocked = ("OTEL_", "ANTHROPIC_", "MODELPROVIDER__", "MODEL_PROVIDER__", "IDENTITY__", "STORAGE__", "ASPNETCORE_")
    env = {name: value for name, value in os.environ.items() if not name.upper().startswith(blocked)}
    env.update(extra)
    return env


def dotnet_command(port: int, customer: str, database: Path) -> tuple[list[str], dict[str, str], Path]:
    project = ROOT / "src" / "dotnet" / "Support.Api"
    command = ["dotnet", "run", "--project", str(project), "-c", "Release", "--no-build", "--no-launch-profile"]
    return (
        command,
        {
            "ASPNETCORE_ENVIRONMENT": "Development",
            "ASPNETCORE_URLS": f"http://127.0.0.1:{port}",
            "Identity__DevelopmentCustomerId": customer,
            "Storage__Path": str(database),
            "ModelProvider__Mode": "Fake",  # overrides any local user-secrets that select Live
            "DOTNET_NOLOGO": "true",
        },
        ROOT,
    )


def python_command(port: int, customer: str, database: Path) -> tuple[list[str], dict[str, str], Path]:
    command = ["uv", "run", "--locked", "uvicorn", "support_agent.app:app", "--host", "127.0.0.1", "--port", str(port)]
    return (
        command,
        {
            "APP_ENVIRONMENT": "development",
            "IDENTITY__DEVELOPMENT_CUSTOMER_ID": customer,
            "STORAGE__PATH": str(database),
            "MODEL_PROVIDER__MODE": "fake",
        },
        ROOT / "src" / "python",
    )


COMMANDS: dict[str, Callable[[int, str, Path], tuple[list[str], dict[str, str], Path]]] = {
    "dotnet": dotnet_command,
    "python": python_command,
}


@dataclass
class Server:
    url: str
    process: subprocess.Popen[bytes]
    log: Path


def wait_healthy(server: Server) -> None:
    deadline = time.monotonic() + STARTUP_SECONDS
    while time.monotonic() < deadline:
        if server.process.poll() is not None:
            raise RuntimeError(f"{server.url} exited during startup; see {server.log}")
        try:
            with urllib.request.urlopen(f"{server.url}/health", timeout=2) as response:
                if response.status == 200:
                    return
        except OSError:
            pass
        time.sleep(0.3)
    raise RuntimeError(f"{server.url} did not become healthy within {STARTUP_SECONDS} s; see {server.log}")


@contextmanager
def servers(backend: str, workdir: Path) -> Iterator[tuple[Server, Server]]:
    """Two processes of one backend on one database file: one per customer."""
    database = workdir / backend / "support.db"
    database.parent.mkdir(parents=True, exist_ok=True)
    started: list[Server] = []
    try:
        for customer in (OWNER, OTHER):
            port = free_port()
            command, extra, cwd = COMMANDS[backend](port, customer, database)
            log = workdir / f"{backend}-{customer}.log"
            with log.open("wb") as sink:
                process = subprocess.Popen(
                    command, cwd=cwd, env=child_environment(extra), stdout=sink, stderr=subprocess.STDOUT
                )
            server = Server(f"http://127.0.0.1:{port}", process, log)
            started.append(server)
            wait_healthy(server)
        yield started[0], started[1]
    finally:
        for server in started:
            server.process.terminate()
        for server in started:
            try:
                server.process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                server.process.kill()


# ---- HTTP -----------------------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class Response:
    status: int
    content_type: str
    headers: dict[str, str]
    text: str

    def json(self) -> Any:
        return json.loads(self.text)


def call(base: str, method: str, path: str, body: Any = None, raw: bytes | None = None, accept: str | None = None) -> Response:
    data = raw if raw is not None else (None if body is None else json.dumps(body).encode())
    request = urllib.request.Request(f"{base}{path}", data=data, method=method)
    if data is not None:
        request.add_header("Content-Type", "application/json")
    if accept:
        request.add_header("Accept", accept)
    try:
        with urllib.request.urlopen(request, timeout=REQUEST_SECONDS) as response:
            payload = response.read()
            status, headers = response.status, response.headers
    except urllib.error.HTTPError as error:
        payload, status, headers = error.read(), error.code, error.headers
    content_type = (headers.get("Content-Type") or "").split(";")[0].strip().lower()
    return Response(status, content_type, {k.lower(): v for k, v in headers.items()}, payload.decode())


# ---- Contract validation ----------------------------------------------------------------------------------------------------


class ContractViolation(AssertionError):
    pass


def check(schema: str, document: Any, where: str) -> None:
    errors = sorted(validator(schema).iter_errors(document), key=lambda e: list(e.absolute_path))
    if errors:
        found = "; ".join(f"/{'/'.join(map(str, e.absolute_path))}: {e.message}" for e in errors[:5])
        raise ContractViolation(f"{where}: does not match {schema}: {found}")


def expect(condition: bool, message: str) -> None:
    if not condition:
        raise ContractViolation(message)


def parse_sse(text: str, where: str) -> list[dict[str, Any]]:
    events: list[dict[str, Any]] = []
    for block in text.replace("\r\n", "\n").strip().split("\n\n"):
        fields = dict(line.split(": ", 1) for line in block.splitlines() if line and not line.startswith(":"))
        if not fields:
            continue
        event = json.loads(fields["data"])
        check("stream-event", event, where)
        expect(fields.get("event") == event["type"], f"{where}: SSE event name differs from the event type")
        expect(fields.get("id") == str(event["sequence"]), f"{where}: SSE id differs from the sequence")
        events.append(event)
    expect([e["sequence"] for e in events] == list(range(1, len(events) + 1)), f"{where}: sequence is not 1..n")
    finals = [e for e in events if e["type"] == "turn.final"]
    expect(len(finals) == 1 and events[-1] is finals[0], f"{where}: not exactly one final event, last")
    expect(events[0]["type"] == "turn.started", f"{where}: the first event is not turn.started")
    return events


# ---- Normalization ------------------------------------------------------------------------------------------------------------

# Runtime ids and times differ by nature; they become stable placeholders (the same id gets the same placeholder within
# one backend's run, so "the same turn came back" stays comparable).
RUNTIME_ID = re.compile(
    r"^(?:(conv|turn|op|appr)_[A-Za-z0-9]+|(RFD|TKT|PAY)-[0-9A-Za-z-]+|[0-9a-f]{32}|"
    r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$"
)
TIMESTAMP = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}")
FIXTURE_PAYMENTS = {"PAY-5001", "PAY-5002", "PAY-5003", "PAY-5004", "PAY-5005", "PAY-5006", "PAY-5007"}
# Wording: the two fake models are separate ports and phrase replies differently, and a problem's or a failure's
# ``detail`` is human text. What a reply must contain is asserted per probe (``mentions``); the contract governs the
# shape, the codes and the facts, not the words.
WORDING = {"text", "reply_text", "detail"}


class Normalizer:
    def __init__(self) -> None:
        self.ids: dict[str, str] = {}

    def placeholder(self, value: str) -> str:
        match = RUNTIME_ID.match(value)
        assert match is not None
        kind = match.group(1) or match.group(2) or ("trace" if len(value) == 32 else "uuid")
        if value not in self.ids:
            self.ids[value] = f"<{kind}:{sum(1 for v in self.ids.values() if v.startswith(f'<{kind}:')) + 1}>"
        return self.ids[value]

    def __call__(self, value: Any, key: str | None = None) -> Any:
        if isinstance(value, dict):
            return {k: self(v, k) for k, v in sorted(value.items())}
        if isinstance(value, list):
            if key == "errors":
                # invalid_request must list what is wrong (contract); how a validator words or splits it is its own.
                return "<errors>" if value else "<no errors>"
            return [self(v) for v in value]
        if isinstance(value, str):
            if key in WORDING:
                return "<text>"
            if key == "backend":
                return "<backend>"
            if key == "trace_id":
                return "<trace>"
            if TIMESTAMP.match(value):
                return "<time>"
            if value not in FIXTURE_PAYMENTS and RUNTIME_ID.match(value):
                return self.placeholder(value)
        return value


# ---- Probes ------------------------------------------------------------------------------------------------------------------


@dataclass
class Run:
    """One backend's probe run: what each probe observed, normalized."""

    owner: str
    other: str
    normalize: Normalizer = field(default_factory=Normalizer)
    observed: dict[str, Any] = field(default_factory=dict[str, Any])

    def record(self, probe: str, response: Response, schema: str | None, document: Any = None) -> Any:
        body = document if document is not None else (response.json() if response.text else None)
        if schema is not None:
            check(schema, body, f"{probe}")
        if response.status >= 400:
            expect(response.content_type == "application/problem+json", f"{probe}: a problem must be problem+json")
            check("problem", body, probe)
        self.observed[probe] = {"status": response.status, "content_type": response.content_type, "body": self.normalize(body)}
        return body


def turn(message: str, client_turn_id: str | None = None) -> dict[str, str]:
    return {"client_turn_id": client_turn_id or str(uuid.uuid4()), "message": message}


def mentions(probe: str, body: dict[str, Any], *words: str) -> None:
    text = body.get("reply", {}).get("text", "")
    for word in words:
        expect(word.lower() in text.lower(), f"{probe}: the reply should mention {word!r}: {text!r}")


def probe_all(run: Run) -> None:
    a, b = run.owner, run.other

    # Conversations: create, read, unknown fields, malformed JSON, another customer's, a missing one.
    created = run.record("create-conversation", call(a, "POST", "/api/v1/conversations", {}), "conversation")
    conversation = created["conversation_id"]
    run.record("get-conversation", call(a, "GET", f"/api/v1/conversations/{conversation}"), "conversation")
    run.record(
        "create-with-customer-id",
        call(a, "POST", "/api/v1/conversations", {"customer_id": OTHER}),
        None,
    )
    run.record("create-malformed-json", call(a, "POST", "/api/v1/conversations", raw=b"{not json"), None)
    foreign = run.record("get-conversation-as-other", call(b, "GET", f"/api/v1/conversations/{conversation}"), None)
    missing = run.record("get-conversation-missing", call(a, "GET", "/api/v1/conversations/conv_doesnotexist0000"), None)
    expect(
        {k: v for k, v in foreign.items() if k != "trace_id"} == {k: v for k, v in missing.items() if k != "trace_id"},
        "another customer's conversation must look exactly like a missing one",
    )

    # A read turn, its replay, a reused id with another message, and another customer posting into it.
    first = turn("Where is my order ORD-1002?")
    answered = run.record("turn-order-status", call(a, "POST", f"/api/v1/conversations/{conversation}/turns", first), "turn-result")
    mentions("turn-order-status", answered, "ORD-1002", "shipped")
    replayed = run.record("turn-replay", call(a, "POST", f"/api/v1/conversations/{conversation}/turns", first), "turn-result")
    expect(replayed == answered, "a repeated client_turn_id must return the stored turn unchanged")
    run.record(
        "turn-reused-id",
        call(a, "POST", f"/api/v1/conversations/{conversation}/turns", turn("Something else", first["client_turn_id"])),
        None,
    )
    run.record(
        "turn-as-other",
        call(b, "POST", f"/api/v1/conversations/{conversation}/turns", turn("Where is my order ORD-1002?")),
        None,
    )
    run.record(
        "get-turn",
        call(a, "GET", f"/api/v1/conversations/{conversation}/turns/{answered['turn_id']}"),
        "turn-result",
    )
    run.record("get-turn-as-other", call(b, "GET", f"/api/v1/conversations/{conversation}/turns/{answered['turn_id']}"), None)
    run.record(
        "cancel-finished-turn",
        call(a, "POST", f"/api/v1/conversations/{conversation}/turns/{answered['turn_id']}/cancel"),
        None,
    )
    run.record(
        "cancel-unknown-turn",
        call(a, "POST", f"/api/v1/conversations/{conversation}/turns/turn_doesnotexist0000/cancel"),
        None,
    )
    run.record(
        "turn-invalid-request",
        call(a, "POST", f"/api/v1/conversations/{conversation}/turns", {"message": "no client turn id"}),
        None,
    )

    # Another customer's order, an ineligible write, and a failed turn: business answers, not HTTP errors.
    foreign_order = run.record(
        "turn-other-customers-order",
        call(a, "POST", f"/api/v1/conversations/{conversation}/turns", turn("Where is my order ORD-2001?")),
        "turn-result",
    )
    expect("delivered" not in foreign_order["reply"]["text"].lower(), "another customer's order status leaked")
    run.record(
        "turn-ineligible-cancel",
        call(a, "POST", f"/api/v1/conversations/{conversation}/turns", turn("Please cancel order ORD-1001.")),
        "turn-result",
    )
    run.record(
        "turn-model-failure",
        call(a, "POST", f"/api/v1/conversations/{conversation}/turns", turn("Where is ORD-1002? [fake:model-error]")),
        "turn-result",
    )

    # An approval-gated write: bindings, approval, replay, and a different decision afterwards.
    asked = run.record(
        "turn-cancel-request",
        call(a, "POST", f"/api/v1/conversations/{conversation}/turns", turn("Please cancel order ORD-1003.")),
        "turn-result",
    )
    pending = asked["pending_approval"]
    operation_path = f"/api/v1/operations/{pending['operation_id']}"
    approval_path = f"/api/v1/conversations/{conversation}/approvals/{pending['approval_id']}"
    approve = {"operation_id": pending["operation_id"], "decision": "approve"}
    run.record("operation-awaiting", call(a, "GET", operation_path), "operation")
    run.record("conversation-pending", call(a, "GET", f"/api/v1/conversations/{conversation}"), "conversation")
    run.record("operation-as-other", call(b, "GET", operation_path), None)
    run.record("approve-as-other", call(b, "POST", approval_path, approve), None)
    run.record(
        "approve-wrong-operation",
        call(a, "POST", approval_path, {"operation_id": "op_doesnotexist0000", "decision": "approve"}),
        None,
    )
    approved = run.record("approve", call(a, "POST", approval_path, approve), "turn-result")
    run.record("operation-committed", call(a, "GET", operation_path), "operation")
    again = run.record("approve-again", call(a, "POST", approval_path, approve), "turn-result")
    expect(again == approved, "repeating a decision must return the same turn")
    run.record(
        "reject-after-approve",
        call(a, "POST", approval_path, {"operation_id": pending["operation_id"], "decision": "reject"}),
        None,
    )

    # Streaming: the same contract as Server-Sent Events.
    streamed = call(
        a,
        "POST",
        f"/api/v1/conversations/{conversation}/turns",
        turn("Where is my order ORD-1002?"),
        accept="text/event-stream",
    )
    expect(streamed.status == 200, f"stream: status {streamed.status}")
    expect(streamed.content_type == "text/event-stream", f"stream: content type {streamed.content_type}")
    expect("no-cache" in streamed.headers.get("cache-control", ""), "stream: must not be cached")
    events = parse_sse(streamed.text, "stream")
    final = events[-1]["data"]["result"]
    last_reset = max((i for i, e in enumerate(events) if e["type"] == "reply.reset"), default=-1)
    text = "".join(e["data"]["text"] for e in events[last_reset + 1 :] if e["type"] == "reply.delta")
    expect(text == final["reply"]["text"], "stream: the deltas after the last reset must equal the final reply text")
    stored = call(a, "GET", f"/api/v1/conversations/{conversation}/turns/{final['turn_id']}").json()
    expect(stored == final, "stream: the final event must carry the stored turn")
    run.observed["stream"] = {
        "status": streamed.status,
        "content_type": streamed.content_type,
        "event_types": sorted({e["type"] for e in events}),
        "stages": [e["data"]["stage"] for e in events if e["type"] == "turn.progress"],
        "final": run.normalize(final),
    }


# ---- Comparison ----------------------------------------------------------------------------------------------------------------

# Differences that are implementation details, each with its reason (also in docs/decisions/0004-sdk-comparison.md).
# Never add a business or security rule here: those must match.
ALLOWED_DIFFERENCES: dict[str, str] = {}


def differences(left: Any, right: Any, path: str = "") -> Iterator[str]:
    if isinstance(left, dict) and isinstance(right, dict):
        for key in sorted(set(left) | set(right)):
            where = f"{path}/{key}"
            if key not in left:
                yield f"{where}: only python has it ({json.dumps(right[key])[:120]})"
            elif key not in right:
                yield f"{where}: only dotnet has it ({json.dumps(left[key])[:120]})"
            else:
                yield from differences(left[key], right[key], where)
    elif isinstance(left, list) and isinstance(right, list) and len(left) == len(right):
        for index, (a, b) in enumerate(zip(left, right, strict=True)):
            yield from differences(a, b, f"{path}/{index}")
    elif left != right:
        yield f"{path}: dotnet {json.dumps(left)[:120]} != python {json.dumps(right)[:120]}"


def compare(observed: dict[str, dict[str, Any]]) -> list[str]:
    dotnet, python = observed["dotnet"], observed["python"]
    found: list[str] = []
    for probe in sorted(set(dotnet) | set(python)):
        for difference in differences(dotnet.get(probe), python.get(probe), probe):
            if not any(difference.startswith(allowed) for allowed in ALLOWED_DIFFERENCES):
                found.append(difference)
    return found


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dotnet-build", action="store_true", help="build the .NET API (Release) before starting it")
    parser.add_argument("--json", type=Path, help="also write every backend's normalized observations to this file")
    args = parser.parse_args(argv)
    if args.dotnet_build:
        subprocess.run(
            ["dotnet", "build", str(ROOT / "src" / "dotnet" / "Support.Api"), "-c", "Release", "--nologo", "-v", "q"],
            check=True,
        )
    workdir = Path(tempfile.mkdtemp(prefix="contract-probes-"))
    observed: dict[str, dict[str, Any]] = {}
    failures: list[str] = []
    for backend in ("dotnet", "python"):
        started = time.monotonic()
        try:
            with servers(backend, workdir) as (owner, other):
                run = Run(owner.url, other.url)
                probe_all(run)
                observed[backend] = run.observed
            print(f"{backend}: {len(run.observed)} probes passed the contract in {time.monotonic() - started:.1f} s")
        except (ContractViolation, RuntimeError) as error:
            failures.append(f"{backend}: {error}")
            print(f"{backend}: FAILED: {error}")
    if args.json is not None:
        args.json.write_text(json.dumps(observed, indent=2, sort_keys=True), encoding="utf-8")
    if not failures:
        drift = compare(observed)
        if drift:
            failures.append("the backends differ:\n  " + "\n  ".join(drift))
        else:
            print(f"dotnet and python agree on all {len(observed['dotnet'])} probes")
    if failures:
        print("\n".join(failures), file=sys.stderr)
        print(f"Server logs: {workdir}", file=sys.stderr)
        return 1
    shutil.rmtree(workdir, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
