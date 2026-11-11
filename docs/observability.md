# Observability

What each support backend sends to Aspire, where each signal comes from, and how to read it. The .NET API was introduced in L06; the Python backend's own telemetry, in L40 (see "The Python backend" below).

## Run it and look

```bash
dotnet run --project apphost        # or: aspire start --apphost apphost/Support.AppHost.csproj
```

Open the dashboard URL that is printed, then send a turn (port 5180):

```bash
C=$(curl -s -X POST localhost:5180/api/v1/conversations -H 'content-type: application/json' -d '{}' | jq -r .conversation_id)
# success
curl -s -X POST localhost:5180/api/v1/conversations/$C/turns -H 'content-type: application/json' \
  -d '{"client_turn_id":"'$(uuidgen)'","message":"Where is my order ORD-1002?"}' | jq '{reply, trace_id}'
# failure (fake mode only: the marker makes the fake model throw)
curl -s -o /dev/null -w '%{http_code}\n' -X POST localhost:5180/api/v1/conversations/$C/turns -H 'content-type: application/json' \
  -d '{"client_turn_id":"'$(uuidgen)'","message":"Where is ORD-1002? [fake:model-error]"}'
```

- **Traces:** the `trace_id` in a turn result is the id of that request's trace. Look it up in Traces, or run `aspire otel spans support-api-dotnet --trace-id <id>`.
- **Structured logs:** open a span and choose "View logs", or run `aspire otel logs support-api-dotnet --trace-id <id>`.
- **Metrics:** Metrics → `support-api-dotnet` → meters `Support.Api`, `Support.Agents`, `Support.Model`.

## One turn, as a trace

```text
POST /api/v1/conversations/{conversationId}/turns   Microsoft.AspNetCore   HTTP status, route
└─ support.turn                                       Support.Api            conversation, client turn and turn ids; outcome; reply kind
   ├─ workflow.build                                  Support.Workflows      the routing workflow is built per turn (see below)
   └─ workflow.session                                Support.Workflows
      └─ invoke_workflow support-routing              Support.Workflows      gen_ai.workflow.name
         ├─ executor.process triage                   Support.Workflows      the routing decision: support.triage.* (outcome, target, reason, action, order_id_from_context)
         │  └─ invoke_agent triage → chat <model>     Support.Agents/Model   one JSON call (gen_ai.output.type=json)
         ├─ executor.process <selected executor>      Support.Workflows      orders-/billing-/technical-specialist, ask-for-order-id, or unrouted
            └─ invoke_agent <specialist>              Support.Agents         only for the three *-specialist executors
               ├─ chat <model>                        Support.Model          one per model call; that call's token usage
               ├─ execute_tool <tool>                 Support.Agents         get_order_status, get_invoice, get_payment_history, search_knowledge_base
               └─ chat <model>                        Support.Model
         └─ executor.process reply                    Support.Workflows      the one output step every ending sends its reply to
```

The triage executor's span carries the routing decision. Its sibling, the selected executor's span, shows what ran because of it. A test checks this link for routed, ambiguous and invalid triage.

On failure, every span from HTTP down to the failing call has error status. `support.turn` also carries `error.type` and an `exception` event.

## DevUI chats

DevUI (Development only) is listed as a **DevUI** link on the `support-api-dotnet` resource in the dashboard, at `http://localhost:5180/devui/`.
- **A chat with the `support-routing` workflow** is traced like an API turn, without `support.turn`: `POST /v1/responses/` → `invoke_workflow support-routing` → `executor.process chat-input` → `triage` → the selected executor → `reply`.
- **A chat with a single agent** (`orders`, `billing`, `technical`) skips triage:

```text
POST /v1/responses/      Microsoft.AspNetCore
└─ invoke_agent <agent>  Support.Agents
   ├─ chat <model>       Support.Model
   ├─ execute_tool <tool>
   └─ chat <model>
```

**Use Aspire for DevUI traces, not DevUI's own Traces panel.**
- DevUI's built-in Traces panel only works with the Python DevUI (`--tracing`); Microsoft's C# DevUI tracing docs are "coming soon". With .NET DevUI 1.28.0-preview the panel shows "Trace retrieval is not available for this server".
- Telemetry is exported only when the API runs **under the AppHost**, which sets `OTEL_EXPORTER_OTLP_ENDPOINT`. An API started on its own still serves DevUI but sends its telemetry nowhere.
- DevUI's Tools panel timestamps appear to show when the panel displayed a call, not when it ran. Use the chat message times, or span start times in Aspire.

It has no `support.turn` span and no `support.turns` metric, because those belong to the contract API's turn handler. Tool calls still count in `support.tool.calls`, and token usage still counts in `gen_ai.client.token.usage`.

## Who owns which signal

There is **one** OpenTelemetry pipeline and **one** OTLP exporter, both in `Support.ServiceDefaults`. The exporter is switched on by `OTEL_EXPORTER_OTLP_ENDPOINT`, which the AppHost sets. Modules only subscribe the pipeline to their own sources. Nobody creates a second provider or exporter.

| Source / meter | Registered by | Emitted by | Signals |
| --- | --- | --- | --- |
| ASP.NET Core, HttpClient, runtime | `Support.ServiceDefaults` | framework instrumentation | HTTP server/client spans and `http.server.request.duration` (route, method, status); runtime metrics. `/health` and `/alive` are not traced. |
| `Support.Api` | `Common/Infrastructure/Observability/SupportTelemetryRegistration` | `SubmitTurnCommandHandler`, `OrderStatusTool`, `TriageExecutor` | `support.turn` span and `support.triage.*` tags; `support.turns`, `support.turn.duration`, `support.tool.calls`, `support.triage.decisions` metrics |
| `Support.Agents` | `Modules/Agents/Infrastructure/Observability/AgentTelemetryRegistration` | MAF `OpenTelemetryAgent`, applied to **every** agent by `SupportAgents` | `invoke_agent` and `execute_tool` spans; whole-run `gen_ai.client.operation.duration` |
| `Support.Model` | same | M.E.AI `OpenTelemetryChatClient`, wrapped around the provider client in `ModelProviderRegistration` | `chat` spans; per-call `gen_ai.client.operation.duration` and `gen_ai.client.token.usage` |
| `Support.Workflows` | same | MAF Workflows `WithOpenTelemetry`, in `SupportWorkflowRouter` (L07) | `invoke_workflow`, `executor.process`, edge and message spans; `workflow.build` per turn |
| Logs (`ILogger`) | `Support.ServiceDefaults` | the handler (turn completed or failed), the tool (lookup outcome), framework categories | structured logs, automatically stamped with the trace and span id |

**Workflow notes (MAF 1.28.0):**
- Workflow spans use the 1.28.0 names: `invoke_workflow {name}` and `gen_ai.workflow.name`. The course predates them.
- `WorkflowTelemetryOptions.EnableSensitiveData` is now an obsolete no-op, so it isn't set.
- Because a fresh workflow is built per turn, every turn also has a `workflow.build` span. Its `workflow.definition` attribute describes the graph (executor ids and types), not customer data.
- **The conversation id is not passed as OpenTelemetry baggage**, even though 1.28.0 reads `gen_ai.conversation.id` from it. .NET forwards Activity baggage as a `baggage` header on outgoing HTTP calls, which would send our conversation ids to the model provider. The id is on `support.turn` instead, which every workflow span sits under.

**Why two AI sources?**
- They keep the layers apart in Aspire: the whole agent run, and each model call within it.
- They let one layer's metric be filtered without touching the other.

Because the model client is already instrumented, MAF does not auto-wire a second chat-level wrapper. If it did, chat spans would be duplicated.

## Token usage is counted once

The agent layer re-records the whole run's tokens in `gen_ai.client.token.usage`, and the model layer has already recorded the same tokens call by call. Summed over meters, every token would count twice. The L06 test proved this: 400 input tokens instead of 200 for two 100-token calls.

**Fix:**
- An OpenTelemetry **View** drops `gen_ai.client.token.usage` from the `Support.Agents` meter.
- The per-call figures on `Support.Model` remain the single source of truth, because the provider bills per call.
- The run total is still visible as attributes on the `invoke_agent` span.

**Known quirk:** MAF 1.28.0 tags the *agent-level* `gen_ai.client.operation.duration` with `gen_ai.operation.name=chat`, the same as real model calls. Tell them apart by meter: `Support.Agents` is a whole run; `Support.Model` is one model call.

## Metrics and their labels

| Metric | Type | Labels (all bounded) |
| --- | --- | --- |
| `support.turns` | counter | `support.agent` (route target, `unrouted`, or `unknown` on failure), `support.turn.outcome` (`completed`, `awaiting_approval`, `failed`, `replayed`), `support.reply_kind` (`answer`, `not_found`, `clarification`, `escalation_offer`, `not_eligible`, `approval_request`, `approval_result`, `none`) |
| `support.turn.duration` (s) | histogram | `support.agent`, `support.turn.outcome` |
| `support.tool.calls` | counter | `support.tool.name` (the four read tools; write tools are counted by `support.operations`), `support.tool.outcome` (`found`, `not_found`, `invalid_order_id`, `invalid_reference`, `no_match`) |
| `support.operations` | counter | `support.operation.kind` (`cancel_order`, `refund_order`, `create_ticket`), `support.operation.outcome` (`awaiting_approval`, `committed`, `failed`, `not_eligible`, `not_found`, `invalid_arguments`) |
| `support.triage.decisions` | counter | `support.triage.outcome` (`routed`, `unrouted`), `support.triage.target` (`orders`, `billing`, `technical`, `none`), `support.triage.unrouted_reason` (`invalid_model_output`, `ambiguous_request`, `none`) |
| `gen_ai.client.token.usage` | histogram | provider, model, `gen_ai.token.type` (`Support.Model` only) |
| `gen_ai.client.operation.duration` (s) | histogram | provider, model, operation |
| `http.server.request.duration` (s) | histogram | route template, method, status code |

Conversation, turn and trace ids are **never** metric labels: each new value would create a new time series. They belong on spans and logs, which a test checks.

## Service identity

| Attribute | Value | Set by |
| --- | --- | --- |
| `service.name` | `support-api-dotnet` | the AppHost (`OTEL_SERVICE_NAME`) |
| `service.instance.id` | per run | the AppHost |
| `service.version` | the assembly's informational version, e.g. `1.0.0+<git commit>` | `SupportTelemetryRegistration` |
| `deployment.environment.name` | ASP.NET environment, e.g. `Development` | `SupportTelemetryRegistration` |

## Sensitive data

**By default telemetry carries no prompts, replies, tool arguments or tool results.** It also carries no customer id, and logs carry no message text. Tests check this, and an export from a real Aspire run confirmed it.

- **Opt in:** `Telemetry:CaptureSensitiveData=true`, local debugging only. It turns on content capture for both AI layers. Treat the backend as holding personal data while it's on.
- **The standard variable can't override it.** The setting is always applied *explicitly*, so `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true` can't switch capture on by accident. Microsoft.Extensions.AI reads that variable once per process. The test suite runs with it on everywhere, so the default-off test proves the explicit setting wins.
- **Order ids** are not logged by the tool. They do appear in the `execute_tool` span only when capture is on.

## Fake mode

The fake model reports fixed token counts (100 input, 20 output per call), so the token and duration metrics have real samples without a Claude key. It also triages by keywords, returning contract JSON, so routing works offline.

Markers, all in fake mode only:
- `[fake:model-error]`: the specialist model throws, producing a failed turn.
- `[fake:triage-malformed]`: triage returns text that isn't JSON.
- `[fake:triage-unknown-route]`: triage names a target the contract doesn't allow.

The last two both produce `unrouted / invalid_model_output`. None of the markers affect Live mode.

## Failures, retries and budgets (L15)

A turn that runs and fails is a result, not an HTTP error: `200` with `outcome: failed`, an `error.code`, safe text, and the turn's `trace_id`. The `support.turn` span carries `support.turn.error_code`, `support.turn.model_calls` and `support.turn.model_attempts`, and its status is Error.

**One retry owner per layer:**

| Layer | Retries? | Owner and bound |
| --- | --- | --- |
| HTTP to Anthropic | **Yes, the only place** | The Anthropic SDK: `Resilience:ModelMaxRetries` = 2 (3 attempts), short exponential backoff, the `retry-after` hint honoured. It retries connection errors, timeouts, 408/409/429/5xx/529, and never 400/401/403/404/422. Each attempt is limited by `ModelAttemptTimeoutSeconds` (30 s). |
| `HttpClient` | No | The SDK's client is created directly, **not** via `IHttpClientFactory`, so the service defaults' standard resilience handler (which would retry again) never wraps it. A test pins attempts at exactly 3. |
| Model calls in a turn | No retries; a budget | `MaxModelCallsPerTurn` = 10 (triage, specialist steps, declines, resume). Over budget → `budget_exhausted`. |
| Tool loop (MAF function invocation) | No retries | `MaxToolIterations` = 4 steps per agent run; `ToolTimeoutSeconds` = 10 per call → `tool_unavailable`; a tool that throws ends the turn (`MaximumConsecutiveErrorsPerRequest` = 0), so the model can't retry it in a loop. |
| The turn | **No** | `TurnTimeoutSeconds` = 90, on the turn's own deadline (the server's `TimeProvider`) → `timeout`. The client may retry with a **new** `client_turn_id`; repeating the same one replays the stored result. |
| Writes | **Never** retried blindly | Only the L11 operation state machine resumes a write, and its guarded, atomic commit means it can't apply twice. |

**Worst case per turn:** at most 10 model calls × 3 attempts = **30 HTTP attempts**, and never longer than the 90-second deadline, whichever comes first. Without the single-owner rule, the SDK (3) × a standard resilience handler (up to 4) would be 12 attempts per call, 120 per turn.

**Cancellation:** `POST …/cancel` cancels the same token the SDK uses for retries, so a turn in backoff makes no new attempt (tested).

**Error codes:**

| Fault | `error.code` |
| --- | --- |
| Provider down (5xx or overloaded, after retries), connection failure | `model_unavailable` |
| Provider rate limit (429 after retries) | `rate_limited`, with `retry_after_seconds` from the provider's hint |
| Attempt timeout (after retries) or turn deadline | `timeout` |
| Tool overran or failed | `tool_unavailable` |
| Model-call budget used up (for example, a tool loop) | `budget_exhausted` |
| Permanent provider error (4xx) or anything unexpected | `internal_error` |

Invalid triage output is not a failure: the turn completes `unrouted` with a clarification (L07).

All limits live in configuration (`Resilience` section, `ResilienceOptions`).

## The Python backend (L40)

The Python processes send their own telemetry to the same dashboard, under their own service names. Nothing is inherited from .NET: Aspire only tells Python where to send. **Instrumentation** creates spans and measurements, **providers** collect them, **exporters** send them over **OTLP**, and the **dashboard** receives them. The Python code owns the first three.

### Run it and look

```bash
aspire start --apphost apphost/Support.AppHost.csproj   # prints the dashboard login link
```

Send turns to `support-api-python` (its URL is on the resource in the dashboard), in the same way as the .NET examples above: a normal question, and one with `[fake:model-error]`. Then:

- **Traces:** Traces → `support-api-python`, or `aspire otel spans support-api-python --trace-id <trace_id from the turn result>`.
- **Structured logs:** `aspire otel logs support-api-python --trace-id <id>`.
- **Metrics:** Metrics → `support-api-python` → meters `support_agent`, `agent_framework`, `fastapi` (the CLI can't export metrics; use the dashboard).

### One turn, as a trace

```text
POST /api/v1/conversations/{conversation_id}/turns   fastapi           HTTP server span (FastAPI's built-in telemetry)
├─ fastapi.dependencies, fastapi.endpoint             fastapi           request handling (the endpoint waits for the turn)
└─ support.turn                                       support_agent     conversation, turn and client turn ids; outcome; agent; reply kind
   ├─ workflow.build                                  agent_framework   the routing workflow is built per turn
   └─ invoke_workflow support-routing                 agent_framework
      ├─ executor.process triage → invoke_agent triage → chat fake-model
      ├─ edge_group.process …, message.send           agent_framework   how the switch delivered the triage result
      ├─ executor.process <selected executor>         agent_framework   orders-/billing-/technical-specialist, ask-for-order-id, or unrouted
      │  └─ invoke_agent <specialist>                 agent_framework
      │     ├─ chat <model>                           agent_framework   one per model call, with that call's token usage
      │     ├─ execute_tool <tool>                    agent_framework
      │     └─ chat <model>
      └─ executor.process reply
```

- The turn runs as its own task (L12/L20), and it inherits the request's trace when it is started, so everything above is **one trace**.
- The contract's `trace_id`, in turn results and problem responses, is that trace's id.
- `/health` and `/alive` are not traced.
- A failed turn: `support.turn` has error status and `error.type`, and a "Turn failed" log record sits on the same trace with its traceback.

### Who owns which signal

| Scope | Set up by | Emits |
| --- | --- | --- |
| providers and OTLP exporters | `support_agent/telemetry.py`, **once per process** | one tracer, meter and logger provider; gRPC OTLP exporters when `OTEL_EXPORTER_OTLP_ENDPOINT` is set |
| `fastapi` | FastAPI 0.162's **built-in** telemetry, on our providers (`FastAPI(telemetry={"exclude": …})`) | HTTP server spans, `http.server.request.duration`, `http.server.active_requests` |
| `agent_framework` | MAF Python 1.25, turned on in `telemetry.py` (`enable_instrumentation`, with message capture set explicitly) | workflow, executor, edge, `invoke_agent`, `chat` and `execute_tool` spans; `gen_ai.client.operation.duration`, `gen_ai.client.token.usage`, `agent_framework.function.invocation.duration` |
| `support_agent` | our code (`api.py`, `tools.py`, `operations.py`, `workflow.py`, `streaming.py`) | the `support.turn` span, the `support.*` metrics, and structured logs |
| Logs | `telemetry.TraceLogHandler`, the only handler, on the root logger | every log record above INFO, stamped with the active trace and span ids |

**Nothing is duplicated** (tested):
- **HTTP:** `opentelemetry-instrumentation-fastapi` is *not* installed. FastAPI already instruments itself, and both would give two server spans per request.
- **Tokens:** MAF Python records token usage once, per model call. It does *not* repeat it at the agent layer, unlike MAF .NET (see "Token usage is counted once" above), so no View is needed. A test checks 3 calls give exactly 3 × 100 input tokens.
- **DevUI:** its executor calls MAF's `configure_otel_providers()` on start-up, which would add a second set of exporters and a hard-coded `agent-framework-server` service name. `telemetry.py` builds our providers first and marks MAF's one-time setup as done, so DevUI's call does nothing. A test makes a DevUI request and checks that the providers, span processors and log handlers are unchanged.
  - That flag (`OBSERVABILITY_SETTINGS._executed_setup`) is private to MAF, and the test fails loudly if a release renames it.
  - DevUI's own `instrumentation_enabled` switch stays off, because it turns sensitive capture on unconditionally.

### Configuration and lifecycle

| Setting | Source | Notes |
| --- | --- | --- |
| `OTEL_EXPORTER_OTLP_ENDPOINT`, `_HEADERS`, `_CERTIFICATE` | the AppHost (`WithOtlpExporter(OtlpProtocol.Grpc)` on both Python resources) | The headers carry the dashboard's API key. They live only in the process environment, and are never committed, printed or logged. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | the AppHost (`grpc`) | Anything else, with an endpoint set, **fails at startup** (`TelemetryConfigurationError`). A gRPC exporter would otherwise send into the void. |
| `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES` | the AppHost (`support-api-python`, `support-devui-python`, instance id) | Defaults to the same names when run alone. `service.version` comes from the package; `deployment.environment.name` from `APP_ENVIRONMENT`. |
| `OTEL_METRIC_EXPORT_INTERVAL` | the AppHost | read by the SDK's periodic metric reader |
| `TELEMETRY__CAPTURE_SENSITIVE_DATA` | our settings (default `false`) | the only way to turn on message capture (see below) |

- **Running on its own** (`uv run python -m support_agent`): with no endpoint, nothing is exported. To send to a collector anyway, set `OTEL_EXPORTER_OTLP_ENDPOINT` (and `_HEADERS` if it needs a key) in your shell. Never commit them.
- **Shutdown:** each host flushes at lifespan end, within 2 s and off the event loop.
- **At process exit,** our hook flushes and closes the providers within **5 s**. The SDK's own exit hooks are turned off (`shutdown_on_exit=False`) because they have no time limit: with an unreachable collector, the retrying gRPC exporters held the process for over a minute (found by a test; now about 5 s).

### Metrics and their labels (Python)

Same names and labels as .NET, wherever the concept is shared:

| Metric | Labels (all bounded) |
| --- | --- |
| `support.turns` | `support.agent` (target, `unrouted`, `unknown`), `support.turn.outcome` (`completed`, `awaiting_approval`, `failed`, `cancelled`), `support.reply_kind` |
| `support.turn.duration` (s) | `support.agent`, `support.turn.outcome` |
| `support.tool.calls` | `support.tool.name` (the four read tools), `support.tool.outcome` (`found`, `not_found`, `invalid_input`, `no_match`) |
| `support.operations` | `support.operation.kind`, `support.operation.outcome` (`awaiting_approval`, `committed`, `failed`, `not_eligible`, `not_found`, `invalid_arguments`) |
| `support.triage.decisions` | `support.triage.outcome`, `support.triage.target`, `support.triage.unrouted_reason` |
| `support.stream.first_delta` (s) | none: time from turn start to the first streamed reply text (L20's latency, now measured continuously) |

Ids are never labels; a test scans every metric data point for id-shaped values. **Usage a provider didn't report is recorded as nothing, not zero:** with usage off, a turn adds no `gen_ai.client.token.usage` samples and no usage attributes on its `chat` spans.

### Checked live under Aspire (2026-11-11, fake mode)

- **Success:** the turn's `trace_id` from the API is the dashboard's trace. It holds 20 spans under `support-api-python`, from `POST …/turns` through `support.turn`, the workflow, triage, the orders specialist, three `chat fake-model` calls and `execute_tool get_order_status`.
- **Failure** (`[fake:model-error]`): `support.turn` and the chain down to the failing model call have error status, while the HTTP span stays OK, since a failed turn is a 200 by contract. Two log records sit on that trace: the exception and "Turn failed".
- **Metrics** (dashboard): meters `agent_framework`, `fastapi`, `support_agent`; `support.turns` showed exactly the 2 turns sent.
- **DevUI:** a chat traced as `support-devui-python` (`POST /v1/responses` → `invoke_agent orders` → `chat`, `execute_tool`, `chat`, each once). No `agent-framework-server` resource exists.
- **No content:** the success trace's exported JSON had no message phrase, customer id or tool result, although Aspire also sets `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`.

### Sensitive data (Python)

Off by default: no prompts, replies, tool arguments or results, customer ids, or message text in spans or logs. A test sends a message with a unique phrase and checks that neither it, the customer id, nor the tool's result appears anywhere in that trace's spans or logs.
- **Opting in** takes `TELEMETRY__CAPTURE_SENSITIVE_DATA=true` (local debugging only).
- MAF's own `ENABLE_SENSITIVE_DATA=true` can't switch it on: `telemetry.py` always passes the setting explicitly (tested in a fresh process).

### Failures, retries and budgets (Python, L42)

The same rules, limits and error codes as .NET's L15 above, in `support_agent/resilience.py`. The limits are `RESILIENCE__*` settings, with the same defaults, and are validated at startup. The `support.turn` span carries `support.turn.model_calls`, `support.turn.model_attempts` and, for a failed turn, `support.turn.error_code`.

**One retry owner per layer:**

| Layer | Retries? | Owner and bound |
| --- | --- | --- |
| HTTP to Anthropic | **Yes, the only place** | The Anthropic SDK client, which `model_provider.py` builds itself (not MAF): `max_retries` = `RESILIENCE__MODEL_MAX_RETRIES` (2, so 3 attempts), with a timeout of `MODEL_ATTEMPT_TIMEOUT_SECONDS` (30 s) per attempt. It honours `retry-after-ms` / `retry-after`. It retries 408/409/429/5xx and connection errors, never 400/401/403/404/422. An SDK middleware (`count_attempts`) counts every attempt for the turn and retries nothing. |
| HTTP transport | No | The SDK's own httpx client; its transport's `retries` is httpx's default of 0. There's no `IHttpClientFactory`-style handler in Python to stack a second layer. A test pins attempts at exactly `retries + 1` for 0, 1, 2 and 4. |
| MAF's Anthropic client | No | It wraps errors (`ChatClientException`) and never retries. |
| Model calls in a turn | No retries; a budget | `ModelCallBudget`, a chat middleware on every agent: the 11th call is refused **before** it reaches the provider → `budget_exhausted`. |
| Tool loop (MAF function invocation) | No retries | `max_iterations` = `MAX_TOOL_ITERATIONS` (4; MAF's default is 40), then one final answer without tools. `ToolLimits`, a function middleware, sets `TOOL_TIMEOUT_SECONDS` (10 s) per call and turns any tool fault into MAF's fail-closed `MiddlewareFailure` → `tool_unavailable`, so the model never retries a broken tool. Argument validation errors still go back to the model to correct, bounded by the loop and the budget. |
| The turn | **No** | `asyncio.timeout(TURN_TIMEOUT_SECONDS)` (90 s) around every turn and every resumed decision → `timeout`. |
| Writes | **Never** retried blindly | Only the L37/L38 operation state machine resumes a write. A deadline before the commit fails the operation with code `timeout`, and nothing changed. After the commit, the committed result stands: a timeout never rolls a write back. |

**Python-specific details:**
- **Sync tools run in a worker thread** (MAF uses `asyncio.to_thread`). A tool timeout stops the turn from waiting, but the thread finishes on its own. All such tools are read-only lookups, so that's harmless. The write tools are async, so a deadline stops them at an await, and the pre-commit boundary refuses to start a transaction.
- **The rate-limit hint** is read from the SDK's `RateLimitError` response, which carries its headers, so no transport hook is needed (in .NET the exception lacks it). It is stored with the turn, so a replay returns it.
- **Cancel and shutdown:** the SDK's backoff is an `anyio.sleep`, so `POST …/cancel` and host shutdown both stop it with no new attempt (tested). The lifespan closes the SDK client after running turns are stored.

**Worst case per turn:** 10 model calls × 3 attempts = 30 HTTP attempts, and never longer than 90 s.

## Not yet covered

- Per-customer rate limiting and a circuit breaker for a provider outage (repeated failures still each spend up to 3 attempts).
