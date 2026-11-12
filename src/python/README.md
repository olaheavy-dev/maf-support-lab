# Python backend (`support_agent`)

The VoltCart support backend in Python. It serves the same version-1 contract as the .NET backend (`contracts/README.md`), built with Microsoft Agent Framework for Python. Its logic is its own: nothing here imports .NET code for telemetry, identity, or authorization. A lint rule and a test enforce that.

Status: the host, agents, per-conversation sessions, approval-gated writes, durable state, a tested cross-customer access matrix, streaming with cooperative cancellation, its own telemetry in Aspire, and bounded retries, timeouts and budgets. It now matches the .NET backend's behaviour.
- **Read-only agents:** triage plus the orders, billing and technical specialists.
- **The routing workflow:** `support-routing`.
- **The version-1 contract API** for conversations and turns (JSON).
- **Python DevUI** (development only).

Each specialist has one write tool (cancel, refund, ticket) that needs the customer's approval of the exact action. Everything a pending approval needs survives a restart, and each write applies at most once.

## How it fits together

| Module | What it does |
| --- | --- |
| `settings.py`, `identity.py`, `dependencies.py` | Validated configuration; the server-owned principal, pinned per turn (`identity.pinned`) |
| `fixtures.py`, `domain.py` | The shared synthetic fixtures (`fixtures/data`), as validated Pydantic records |
| `services.py` | Owner-checked, read-only lookups. Another customer's record looks exactly like a missing one. |
| `tools.py` | The agents' tools (`@tool`). Ids only; never a customer id. Each records its outcome in `activity.py`. |
| `agents.py` | Triage plus three specialists, each with only its own tools (ported .NET instructions) |
| `routing.py`, `contract.py` | Triage output validated against the **full** contract schema, then deterministic routing rules |
| `workflow.py` | MAF workflow: triage → switch → ask-for-order-id / specialist / unrouted → reply |
| `api.py`, `conversations.py` | The contract API: schema-validated requests, the same 404 for foreign or missing, `client_turn_id` replay, `failed` results |
| `fake_model.py`, `model_provider.py` | A deterministic fake model on MAF's real chat-client layers; Claude in live mode |
| `devui.py` | Development-only DevUI process over the same agents and services |
| `operations.py`, `write_tools.py`, `approval_gate.py`, `policy.py`, `state.py` | Bound approvals: operations, the approval-required write tools, the gate that turns a native approval request into an operation (or declines it), the eligibility rules, and the simulated state the writes change |
| `telemetry.py` | This process's OpenTelemetry providers and OTLP/gRPC exporters, the `support.*` metrics, and the log handler |
| `resilience.py`, `model_provider.py` | Bounded turns: the per-turn model-call budget, tool time limits, the SDK attempt counter, and failure classification; the Anthropic SDK client built with our retry and timeout limits |
| `streaming.py`, `turn_control.py` | Server-Sent Events for a turn; per-turn cooperative cancellation and live events |
| `database.py` | The local SQLite store: one file per backend and run, `atomically()` transactions |
| `sessions.py` | Per-conversation native `AgentSession`s (stored after each run), `BoundedHistory` (12 plain messages; no tool traffic or notes), and the verified `ConversationContext` |

## Conversation memory

- **The agents are shared definitions; each conversation owns its state.** That means one native MAF `AgentSession` per specialist (created on first use and reused on later turns) and a `ConversationContext`. Nothing is global.
- **Bounded history:** a session stores only plain user and assistant messages, at most 12. Tool calls and results are dropped, so trimming never orphans them; context notes are never stored. MAF keeps them under the provider's source id in the session state.
- **Verified context only:** a follow-up without an id uses the order a **tool found for this customer**, never one that was merely mentioned. The specialist gets it as a one-turn, code-written note, placed right after the customer's message. Triage gets the previous route as a hint.
- **Isolation:** sessions are reached only through an owned conversation. Another customer gets `404` and can't continue it.
- **One turn at a time:** a different turn while one runs gets `409 turn_in_progress` (not queued), so a session's history is never written by two runs at once. A duplicate waits for the one run.

## Prerequisites

- Python 3.15.2 and uv 0.13.7 (see `docs/decisions/0001-version-baseline.md`).

## Run it on its own (no Aspire, no secrets)

```bash
cd src/python
uv sync --locked
APP_ENVIRONMENT=development IDENTITY__DEVELOPMENT_CUSTOMER_ID=CUST-1001 uv run python -m support_agent
# → http://127.0.0.1:5280/health  ("Healthy")
# → http://127.0.0.1:5280/dev/whoami  ({"customer_id": "CUST-1001"})
```

`PORT` and `HOST` change where it listens. Fake mode is the default, so no provider key is needed and no billable call is made.

## Run it with Aspire

`aspire start --apphost apphost/Support.AppHost.csproj` starts both backends. The AppHost runs `uv sync --locked` for this project, then starts Uvicorn with `support_agent.app:app`. It sets `APP_ENVIRONMENT=development` and the development customer, and checks `/health`.

## Approvals

1. "Please cancel order ORD-1003." The model calls `cancel_order`. MAF does **not** run it: it pauses with a `function_approval_request`.
2. The gate checks the arguments, ownership and eligibility *before* asking. Then it creates an `Operation` bound to the customer, conversation, kind and exact arguments (15-minute expiry). The turn ends `awaiting_approval` with `pending_approval`.
   - An ineligible or foreign request is declined natively instead, and the reply states the factual reason (`not_eligible` / `not_found`).
3. `POST /api/v1/conversations/{id}/approvals/{approval_id}` with `{operation_id, decision}` goes through the bindings:
   - another customer or conversation → `404`;
   - wrong operation → `409 approval_mismatch`;
   - a different decision later → `409 approval_already_decided`;
   - too late → `410 approval_expired`;
   - the same decision again → the same turn.
4. **Approve:** the native response, inside `approval_scope(operation)`. The tool runs only for that customer, kind and exact arguments, once; Orders, Billing or Technical re-check eligibility; the result is `committed` (or `failed`).
   **Reject:** the tool never runs → `rejected`.
5. `GET /api/v1/operations/{id}` is the authoritative status.

The tool call is re-sent before each native answer: stored history is plain text, and Anthropic rejects a tool result that doesn't follow its call. No note may sit between them. Both rules were found live; the fake model now enforces Anthropic's pairing rule.

## Telemetry

Under the AppHost, both Python processes send traces, logs and metrics to the Aspire dashboard over OTLP/gRPC, as `support-api-python` and `support-devui-python`. Each turn is one trace (HTTP → `support.turn` → workflow → agents → model calls → tools), and the contract's `trace_id` is that trace's id. Run on its own, nothing is exported unless `OTEL_EXPORTER_OTLP_ENDPOINT` is set.
- **Off by default:** message text, tool data and customer ids; opting in takes `TELEMETRY__CAPTURE_SENSITIVE_DATA=true`.
- **Fails fast:** a protocol other than gRPC fails at startup.
- **Exit is bounded:** exit never waits more than about 5 s for an unreachable collector.

Ownership, names, labels and the live check are in `docs/observability.md` ("The Python backend").

## Retries, timeouts and budgets

Every turn is bounded, with the same limits and error codes as .NET. The full retry inventory is in `docs/observability.md` ("Failures, retries and budgets (Python)").

- **One retry owner:** the Anthropic SDK, which we build with `max_retries` (2) and a 30 s limit per attempt. Nothing else retries a model call, a tool, a turn or a write.
- **Budgets per turn:** at most 10 model calls (refused before reaching the provider), 4 tool rounds per agent run, 10 s per tool call, and 1024 output tokens per call. A turn or resumed decision has a 90 s deadline.
- **Failures are results:** `failed` with `model_unavailable`, `rate_limited` (with `retry_after_seconds`), `timeout`, `tool_unavailable`, `budget_exhausted`, or `internal_error`. The detail is fixed, safe text; the error itself goes to the log and the trace.
- **Writes:** a deadline before the commit fails the operation (`timeout`) and nothing changes. After the commit, the result stands. Retrying the decision replays the stored turn.
- **Tested without a network:** `tests/fake_anthropic.py` is a fake Anthropic HTTP API under the real SDK, so its real retries, backoff, `retry-after` handling and attempt timeouts run in the tests.

## Streaming and cancellation

Send `Accept: text/event-stream` to `POST …/turns` or `POST …/approvals/{id}` to get the turn as Server-Sent Events (contract 1.1). Without that header the response is one JSON `turn-result`, as before.

- **Events, in order:**
  - `turn.started`;
  - `turn.progress`: the stage (`triage`, `routed`, `specialist`, `tool_call`, `awaiting_approval`, `executing_operation`), plus a route or tool name, never message content;
  - `reply.delta`: the model's words **as they arrive**;
  - exactly one `turn.final`, carrying the stored turn.

  If the reply the server settles on differs from what streamed (an approval request, a declined write, a failure or a cancellation), `reply.reset` comes first and then the real text.
- **How it's built:**
  - the specialists run `agent.run(stream=True)`, and the workflow's native `data` events carry progress and text;
  - `run_turn` reads `workflow.run(stream=True)`;
  - FastAPI's `EventSourceResponse` writes the events.
- **Buffering is explicit:** nothing is buffered in the app, and responses send `Cache-Control: no-cache` and `X-Accel-Buffering: no`. A quiet stream gets a `: keepalive` comment every 15 seconds.
- **Cancellation is cooperative:** `POST …/turns/{id}/cancel` returns `202`, or `409 turn_already_finished`, or `404`. It sets the turn's cancel flag and cancels its task, so an in-flight model call stops at its next await.
  - The **pre-commit boundary** is in `OperationService.execute_approved`: once cancellation was requested, no write starts.
  - Cancelled before the commit: the turn is `cancelled`, the operation `failed` with code `cancelled`, and nothing changed.
  - Cancelled after the commit: the committed result stands, and the turn completes from the record.
- **A disconnect is not a cancellation.** The stream detaches; the turn runs on and is stored. Recover with `GET …/turns/{id}` and `GET /api/v1/operations/{id}`. Deltas are not replayed.
- **Shutdown** cancels running turns and waits for them to be stored before the database and clients close.

## Durable state

One SQLite file per backend and run (`STORAGE__PATH`, default `.data/python/support.db`, ignored by Git; never the .NET file). The design and its limits are in `docs/decisions/0003-state-and-idempotency.md`.

- **What survives a restart:**
  - conversations and their verified context;
  - finished turns, so a repeated `client_turn_id` or decision replays;
  - operations, with their binding, expiry and status;
  - the paused native `function_approval_request`;
  - each specialist's `AgentSession`;
  - the simulated business state.

  A pending approval can be approved after a restart, and the paused run resumes in the restored session.
- **At most once:**
  - a write and its `committed` record are one transaction;
  - every status change is guarded on the stored status;
  - the first stored decision wins, and so does the first stored turn.

  Duplicates, concurrent approvals (even from two hosts on one file), and a retry after a crash right after the commit all give one mutation.
- **Recovery:** a retried decision whose write already committed is answered from the record (no model call). One whose write never ran resumes once.
- **Not guaranteed:** exactly-once against a real external provider. That needs the provider's idempotency key, or intent/outcome records with reconciliation.

## DevUI (development only)

```bash
APP_ENVIRONMENT=development IDENTITY__DEVELOPMENT_CUSTOMER_ID=CUST-1001 uv run python -m support_agent.devui
# → http://127.0.0.1:5290  (orders, billing, technical, support-routing)
```

DevUI runs as its own process on loopback (port 5290 by default, or `PORT`), and the AppHost starts it as `support-devui-python`.
- **Why a separate process:** DevUI's UI calls `/v1/...` at the root, and a mounted sub-app's lifespan doesn't run.
- **Its customer:** DevUI has no request identity, so this process serves the one configured development customer (`set_process_principal`). The API process never uses that fallback: every API turn pins the caller, and anything unpinned fails closed.
- **Its guards:** it refuses to start outside development, and rejects requests not addressed to loopback (DevUI's own guard).

## Configuration

Read from the environment, validated before serving (`support_agent/settings.py`). Names mirror the .NET sections, with `__` as the separator:

| Variable | Meaning |
| --- | --- |
| `APP_ENVIRONMENT` | `development` or `production` (the default). Outside development there is no identity provider and `/dev/whoami` doesn't exist. |
| `IDENTITY__DEVELOPMENT_CUSTOMER_ID` | Required in development: the fixed local customer (e.g. `CUST-1001`). **Not authentication.** |
| `MODEL_PROVIDER__MODE` | `fake` (default) or `live`. |
| `MODEL_PROVIDER__ANTHROPIC__MODEL` | Required in live mode (e.g. `claude-haiku-5-5`). |
| `TELEMETRY__CAPTURE_SENSITIVE_DATA` | `false` (default). `true` puts prompts, replies and tool data into spans: local debugging only. |
| `OTEL_*` | Set by the AppHost (endpoint, gRPC, headers with the dashboard key, service name). Never commit them. |
| `ANTHROPIC_API_KEY` | Required in live mode. Secret: environment or Aspire parameter only; never committed, logged, or returned. |
| `STORAGE__PATH` | The SQLite file (default `.data/python/support.db`). Give each run or evaluation its own file. |
| `RESILIENCE__*` | Turn limits, validated at startup: `TURN_TIMEOUT_SECONDS` (90), `MAX_MODEL_CALLS_PER_TURN` (10), `MAX_TOOL_ITERATIONS` (4), `TOOL_TIMEOUT_SECONDS` (10), `MODEL_MAX_RETRIES` (2), `MODEL_ATTEMPT_TIMEOUT_SECONDS` (30), `MAX_OUTPUT_TOKENS` (1024). |

## Identity

The app learns the customer only through `get_principal` → `PrincipalSource`.
- Headers, query strings, bodies, and model output are never consulted.
- Tests run as another customer through `app.dependency_overrides[get_principal_source]`, a server-side injection point no client can reach.
- **Access matrix:** `tests/test_access_matrix.py` proves, in both directions, that every route, tool, service, write, DevUI entry, and restored record denies the other customer exactly like a missing one, with zero mutation. See `docs/threat-model.md`.

## Clients and credentials: who cleans up

The FastAPI **lifespan owns every long-lived resource**. A client (the model client, and the database) is created once, registered with `app.state.resources.own(name, close)`, and closed exactly once, newest first, at shutdown. No request creates its own long-lived client. The API key lives only in validated settings (`SecretStr`), and is passed to the client that needs it.

## Live check (opt-in, costs a small amount)

```bash
ANTHROPIC_API_KEY=... uv run pytest -m live   # the shared scenarios with claude-haiku-5-5
```

## Checks (the same as CI)

```bash
uv sync --locked          # the lock file must match pyproject.toml
uv run ruff check .       # lint (includes the "no .NET imports" rule)
uv run ruff format --check .
uv run pyright            # strict type checking
uv run pytest
```

`pyproject.toml` declares what the project needs (direct dependencies, with exact pins). `uv.lock` records the full resolved tree, transitive packages included, with hashes. `--locked` fails if the two disagree, so CI and every machine install exactly the same packages.
