# Observability (.NET backend)

What the .NET support API sends to Aspire, where each signal comes from, and how to read it. Introduced in L06. The Python backend adds its own in L40.

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
         ├─ executor.process triage                   Support.Workflows      the routing decision: support.triage.* (outcome, target, reason, action)
         │  └─ invoke_agent triage → chat <model>     Support.Agents/Model   one JSON call (gen_ai.output.type=json)
         ├─ executor.process <selected executor>      Support.Workflows      orders-/billing-/technical-specialist, ask-for-order-id, write-not-available, or unrouted
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
| `support.turns` | counter | `support.agent` (route target, `unrouted`, or `unknown` on failure), `support.turn.outcome` (`completed`, `failed`, `replayed`), `support.reply_kind` (`answer`, `not_found`, `clarification`, `escalation_offer`, `none`) |
| `support.turn.duration` (s) | histogram | `support.agent`, `support.turn.outcome` |
| `support.tool.calls` | counter | `support.tool.name` (the four read tools), `support.tool.outcome` (`found`, `not_found`, `invalid_order_id`, `invalid_reference`, `no_match`) |
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

## Not yet covered

- A failed turn currently returns a plain HTTP 500. Mapping it to the contract's failed turn result belongs with resilience work (L15).
