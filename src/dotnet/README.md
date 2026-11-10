# .NET support API

The C# implementation of the support system, run locally through the Aspire AppHost in `apphost/`.

| Project | Purpose |
| --- | --- |
| `Support.Api` | ASP.NET Core host, organised into modules and layers (see [Code layout](#code-layout)): contract API, orders agent and tool, fixtures, model mode, identity, health, Development-only DevUI. |
| `Support.ServiceDefaults` | Shared Aspire defaults: OpenTelemetry export, `/health` and `/alive`, service discovery, HTTP resilience. |
| `Support.Api.Tests` | Integration tests through the ASP.NET Core test host and ArchUnitNET architecture tests (xUnit v3, Microsoft Testing Platform). Folders mirror the API's modules. |
| `../../apphost` | Aspire AppHost: starts the API, shows it in the dashboard, and watches `/health`. |

Versions are pinned in `global.json` (SDK) and `Directory.Packages.props` (packages) at the repository root, per [the version baseline](../../docs/decisions/0001-version-baseline.md).

## Code layout

One project, with folders as layers. Namespaces follow folders. The full reasoning, and what was deliberately not copied from SabiTutor, is in [0002-code-layout](../../docs/decisions/0002-code-layout.md).

```text
Support.Api/
├─ Program.cs                 composition root: each module's Add…Module() and Map…Module()
├─ Common/
│  ├─ Domain/                 Result, Error, ErrorType
│  ├─ Application/            ICommand/IQuery + handler interfaces, TraceIds, SupportTelemetry (span/meter name)
│  ├─ Infrastructure/         SupportTelemetryRegistration (subscribes the OTel pipeline, service version);
│  │                          Persistence/ SupportDatabase (SQLite file, schema, atomic unit of work; L11)
│  └─ Presentation/           ContractJson, ApiResults (every problem response), Result.Match
└─ Modules/
   ├─ Conversations/          the contract API
   │  ├─ Domain/              Conversation (idempotent turn reservation), Turn, ReplyKind
   │  ├─ Application/         CreateConversation, GetConversation, SubmitTurn, GetTurn handlers; IConversationStore
   │  ├─ Infrastructure/      SqliteConversationStore  ← storage lives here (SQLite since L11)
   │  └─ Presentation/        ConversationEndpoints (thin), ConversationRequests (contract validation), contract shapes
   ├─ Orders/
   │  ├─ Domain/              Order, OrderId
   │  ├─ Application/         GetOrderStatus query + handler (ownership check); IOrderRepository
   │  ├─ Infrastructure/      FixtureOrderRepository (fixtures/data/orders.json), OrdersAgentFactory (the orders agent)
   │  └─ Presentation/        OrderStatusTool: the model's "endpoint"
   ├─ Billing/                (L08) read-only: GetInvoice (INV- or ORD- id) and GetPaymentHistory, owner-checked;
   │                          BillingTools (get_invoice, get_payment_history), BillingAgentFactory
   ├─ Technical/              (L08) read-only: keyword knowledge search; KnowledgeSearchTool (search_knowledge_base),
   │                          TechnicalAgentFactory. No ticket tool until L10.
   ├─ Triage/                 (L07) every message is triaged, then routed by code
   │  ├─ Domain/              TriageDecision, RouteTarget, RoutingRules (action must match target), TurnRoute, SupportReply
   │  ├─ Application/         TriageDecisionParser (full contract validation), TriageOutcome, ISupportRouter, TriageMetrics
   │  └─ Infrastructure/      TriageAgentFactory (JSON-only classifier, no tools); Routing/ (the MAF workflow and executors)
   ├─ Identity/               SupportPrincipal, IPrincipalSource, DevelopmentPrincipalSource, /dev/whoami
   └─ Agents/                 shared AI runtime, depends on no other module: SupportAgents (adds agent telemetry),
                              TurnActivity, ToolMetrics (Application); model provider, FakeChatClient, agent/model
                              telemetry registration (Infrastructure); DevUI (Presentation)
```

| Layer | Holds | Must not use |
| --- | --- | --- |
| Domain | Entities and rules with no I/O | Application, Infrastructure, Presentation |
| Application | One command or query and one sealed handler per use case; interfaces for storage | Infrastructure, Presentation |
| Infrastructure | Anything that does I/O: storage, fixtures, the model provider | (anything) |
| Presentation | HTTP endpoints and agent tools: translate in, call a handler, translate out | Infrastructure |

`Support.Api.Tests/Architecture` enforces this with ArchUnitNET:
- the table above;
- sealed `…Handler` classes, commands, and queries live in Application;
- no type sits outside a layer;
- the Agents module depends on no other module. Feature modules plug their agents in by registering a `SupportAgent`.

`RuleSelfTests` proves that each rule fails on deliberately broken fixtures.

**Adding a use case:**
1. Add a `…Command`/`…Query` record and a sealed `internal` handler under `Modules/<X>/Application/<UseCase>/`.
2. Register the handler in `<X>Module.cs`.
3. Call it from a thin endpoint or tool in `Presentation/`.
4. Put new storage behind an Application interface implemented in `Infrastructure/`.

## Prerequisites

- .NET SDK 10.0.403 (`dotnet --version`). `global.json` allows newer 10.0.4xx patches.
- No Docker and no Claude key are needed for fake mode.
- Optional: Aspire CLI 13.6.4 (`aspire --version`) for `aspire run`. The build itself doesn't need it.

## Run

```bash
# Everything through Aspire (dashboard URL is printed; open it to see resources, health, logs, traces)
dotnet run --project apphost

# Or the API alone
dotnet run --project src/dotnet/Support.Api

# Tests
dotnet test --solution maf-support-lab.slnx
```

Package versions are locked: after changing a version in `Directory.Packages.props`, run `dotnet restore` and commit the updated `packages.lock.json` files. CI restores in locked mode and fails on drift. See [testing](../../docs/testing.md).

With the API running (port 5180 in the `http` profile): `GET /health` returns `Healthy`, and in Development `GET /dev/whoami` shows the server-side principal.

## Model mode

| Setting | Default | Notes |
| --- | --- | --- |
| `ModelProvider:Mode` | `Fake` | `Fake` needs no key and makes no billable calls. `Live` uses Claude. |
| `ModelProvider:Anthropic:Model` | `claude-haiku-5-5` | Change only on recorded evidence (see the version baseline). |
| `ModelProvider:Anthropic:ApiKey` | — | Secret. Use `ANTHROPIC_API_KEY` or user-secrets; never commit it. |

```bash
# Opt in to live mode locally (stored outside the repository)
dotnet user-secrets --project src/dotnet/Support.Api set "ModelProvider:Anthropic:ApiKey" "<your key>"
ModelProvider__Mode=Live dotnet run --project src/dotnet/Support.Api
```

In Live mode with a missing model or key, the API refuses to start and names the missing setting. Error messages never include configured values. The `model-provider` health check reports the mode, never a secret.

## Observability

Traces, metrics, and structured logs go to the Aspire dashboard over OTLP. Each turn is one trace:
- HTTP;
- `support.turn`;
- `invoke_agent`;
- model `chat` calls and `execute_tool`.

The turn result's `trace_id` finds it. Token usage is counted once, and metric labels are bounded. Message text and customer ids stay out of telemetry unless `Telemetry:CaptureSensitiveData=true` (local debugging only).

In fake mode, a message containing `[fake:model-error]` produces a failed turn to inspect. Details, sources, and ownership: [docs/observability.md](../../docs/observability.md).

## Identity

- The API gets the customer **only** from `IPrincipalSource`.
- In Development that is a fixed customer from server configuration (`Identity:DevelopmentCustomerId`, `CUST-1001`).
- Headers, query strings, request bodies, and model output are never used for identity. Tests prove headers and query strings are ignored.
- Tests run as other customers by replacing `IPrincipalSource` in the test host. That injection point is in server code, so a client can't reach it.
- Outside Development there is no identity provider yet, and resolving a principal fails closed.
- This is **not** authentication. Production authentication is out of scope (see `docs/threat-model.md`).

## Contract API (version 1)

Implemented so far (see [contracts/README.md](../../contracts/README.md)):
- `POST /api/v1/conversations` and `GET /api/v1/conversations/{id}`;
- `POST /api/v1/conversations/{id}/turns`: JSON responses only;
- `GET /api/v1/conversations/{id}/turns/{turn_id}`;
- `POST /api/v1/conversations/{id}/approvals/{approval_id}`: the customer's decision, which resumes the paused write as a new turn (L10);
- `GET /api/v1/operations/{operation_id}`: the authoritative status of a write (L10).

The rules:
- Requests with unknown fields (such as `customer_id`) get `400 invalid_request`.
- Someone else's conversation or turn returns `404 not_found`.
- Re-sending a `client_turn_id` returns the original turn; reusing it with a different message returns `409 client_turn_id_reused`.
- **One turn at a time per conversation:**
  - while a turn runs, `GET /conversations/{id}` shows its `active_turn_id`;
  - a *different* turn is rejected with `409 turn_in_progress`. It is not queued, so the client decides whether to retry.
  - Other conversations aren't affected.

Since L11 conversations, finished turns, approvals and simulated changes survive a restart (see "Durable state" below). Not yet implemented: streaming (L12) and turn cancellation.

```bash
curl -s -X POST localhost:5180/api/v1/conversations -H 'content-type: application/json' -d '{}'
curl -s -X POST localhost:5180/api/v1/conversations/<conversation_id>/turns -H 'content-type: application/json' \
  -d '{"client_turn_id":"'$(uuidgen)'","message":"Where is my order ORD-1002?"}'
```

## Routing (L07)

Every turn runs the `support-routing` workflow (MAF Workflows 1.28.0, built per turn). The first case that matches wins:

```text
triage ─┬─ orders, order-specific, no id     ─▶ ask-for-order-id      (clarification, no model call)
        ├─ orders                            ─▶ orders-specialist     (get_order_status; cancel_order needs approval)
        ├─ billing                           ─▶ billing-specialist    (get_invoice, get_payment_history; refund_order needs approval)
        ├─ technical                         ─▶ technical-specialist  (search_knowledge_base; create_ticket needs approval)
        └─ default                           ─▶ unrouted              (clarification; route.status = unrouted)
                                                     └─▶ every ending sends its reply to one `reply` executor (the workflow's output)
```

**Each specialist has only its own tools.** Every tool that can change data is a native `ApprovalRequiredAIFunction`, and no read tool is. A test checks this on the agents the app actually built.

The reply kind comes from what the tools recorded, not from the model's wording:
- any record found → `answer`;
- a record not found (including another customer's) → `not_found`;
- a knowledge search with no match → `escalation_offer` (a human is offered, nothing is created);
- no lookup → `clarification`.

**The model classifies; code decides:**
- **Triage** is a JSON-only agent with no tools. Its output is validated against the **full** `contracts/schemas/triage-decision.schema.json`. A test keeps the C# validator in agreement with the schema on the contract examples and on edge cases.
- **Invalid output** (not JSON, extra or missing fields, values outside the allowed sets) is `unrouted / invalid_model_output`.
- **A valid answer can still be ambiguous.** If the action is `other`, or doesn't belong to the chosen target (e.g. `invoice` → `orders`), the result is `unrouted / ambiguous_request`.
- **`confidence` is recorded on the triage span only.** It never affects routing: it is model-generated and uncalibrated.
- **The specialist receives the original message**, alongside the validated fields. The customer stays in trusted server context, and the triage `order_id` is only a claim: ownership is still checked by the orders handler.

## Approvals (L10)

Every simulated write (cancel an order, refund an order, open a ticket) waits for the customer's approval of the **exact** action. The model can ask; only the customer can approve; and the service decides whether the write is allowed.

```text
"Please cancel ORD-1003"
  → orders agent calls cancel_order(ORD-1003)
  → MAF's function invocation pauses with a native approval request (the tool has NOT run)
  → ApprovalGate (Triage/Infrastructure/Routing):
       ├─ invalid / not yours / not eligible → decline natively with the reason → reply not_found / not_eligible
       └─ eligible → Operation op_… "awaiting_approval": bound to customer, conversation, kind, exact arguments,
                     approval appr_…, expires in 15 minutes → turn outcome awaiting_approval
POST …/approvals/appr_… { "operation_id": "op_…", "decision": "approve" | "reject" }
  → DecideApprovalCommandHandler:
      other customer or conversation → 404; wrong operation_id → 409 approval_mismatch;
      different decision later → 409 approval_already_decided; too late → 410 approval_expired;
      same decision again → the same turn (never runs twice)
  → the resumed turn (resumed_from_turn_id) answers the native request in the same session:
       approve → the tool runs inside ApprovalScope(this operation) → OperationService checks kind, exact
                 arguments, and customer → the owning module re-checks eligibility → committed or failed
       reject  → the tool never runs → rejected; any new request in that run is declined (bounded)
```

**Where each rule lives:**
- **Eligibility:** `OrderRules` (cancel only `processing`; refund only `delivered`, within 30 days of the fixtures' reference time, not already refunded), using `fixtures/data/policies.json`.
- **Writes:** each module performs its own: `CancelOrderWriteHandler` (Orders), `RefundOrderWriteHandler` (Billing: full refund, `RFD-…`, a refund payment), `CreateTicketWriteHandler` (Technical: `TKT-…`). They change simulated state in the local SQLite file, atomically with the operation's `committed` record (L11), and never touch the fixture files.
- **The approval record:** `Operations` module (`Operation`, `OperationService`, `GET /api/v1/operations/{id}`).
- **No model-supplied authority:** no tool takes a customer id or a "confirmed" flag.
- **History:** native approval requests and responses are dropped from conversation history, because approvals are operation state, not chat.

## Conversation memory (L09)

The agents are shared singletons; **a conversation's state lives with the conversation**, never in an agent object:
- **Native sessions:** each conversation has its own MAF `AgentSession` per specialist, created on first use and reused on later turns (`Triage/Infrastructure/Routing/ConversationSessions.cs`).
- **Bounded history:** a specialist keeps the last **12** plain conversation messages (`ConversationHistory.MaxMessages`, `BoundedHistoryReducer`).
  - Tool calls and results are dropped from history, so trimming never separates them. The specialist re-reads fresh data with its tools.
  - The storage filter keeps MAF's default of not re-saving messages that came from history. A custom filter replaces that default; forgetting this duplicated history every turn until a test caught it.
- **Verified context (application state, not history):** the conversation remembers its last route and a **verified order id**, meaning one a tool *found for this customer*, never one that was merely mentioned. On a follow-up that names no order:
  - the routing rules use the verified id, so there's no "which order?" question;
  - the specialist gets a one-turn context note right after the customer's message. Anthropic accepts a mid-conversation system message only after a user message.

  Triage stays stateless and gets the previous route as a hint.
- **Isolation:** sessions are reached only through an owned conversation (another customer gets `404`). Separate conversations share nothing.
- **Limits:** DevUI workflow chats are stateless. Sessions are stored after every run since L11, with no expiry yet.

## Durable state (L11)

Everything a conversation needs survives a restart. It lives in one local SQLite file: `Storage:Path`, by default `.data/dotnet/support.db`, which Git ignores. Delete the file to start fresh. The Python backend and each evaluation run use their own file.

- **Stored:**
  - conversations (owner, verified context) and their finished turns, so a repeated `client_turn_id` or decision replays;
  - operations and approvals (exact arguments, expiry, status);
  - the paused native approval request;
  - each specialist's `AgentSession` (MAF `SerializeSessionAsync`);
  - the simulated changes: cancelled orders, refund payments, tickets and their id counters.
- **Agents have stable ids** (`orders`, `billing`, `technical`, `triage`), so a stored session belongs to the same agent after a restart.
- **One write, at most once:**
  - the business change and `status = 'committed'` are one SQLite transaction;
  - every status change is guarded (`… WHERE status = @expected`). A duplicate, a retry or a second host that finds the operation already finished rolls its whole write back.
  - After a crash, a retried decision is answered from the stored record if the operation already finished. If it's still `running`, the paused request is resumed, which is safe because nothing was applied.
- **No workflow checkpoints:** the workflow never pauses mid-run, because an approval ends the turn. See `docs/decisions/0003-state-and-idempotency.md`, which also explains why this is *not* exactly-once against a real external provider.

## Agents, tools, and the model boundary

- **`orders` agent** (`Modules/Orders/Infrastructure/OrdersAgentFactory.cs`, registered by `OrdersModule` as a `SupportAgent`): built once and shared by the API and DevUI. It has a single tool.
- **`billing` and `technical` agents** (`Modules/Billing/Infrastructure/BillingAgentFactory.cs`, `Modules/Technical/Infrastructure/TechnicalAgentFactory.cs`):
  - read-only tools that take ids or search text, never a customer id;
  - their instructions require citing the invoice, order, payment or article id;
  - they can never claim a refund, change or ticket happened.
- **`get_order_status` tool** (`Modules/Orders/Presentation/OrderStatusTool.cs`):
  - The model supplies only the order id.
  - The tool sends a `GetOrderStatusQuery`. Its handler returns the order only if it belongs to the current principal; anything else looks exactly like a missing order.
  - The tool records what it found. The API sets `reply.kind` (`answer`, `not_found`, `clarification`) from those facts, not from the model's wording.
- **Fake model** (`Modules/Agents/Infrastructure/ModelProvider/FakeChatClient.cs`): used in Fake mode and tests. It requests the tool when the message contains an order id, answers from the tool result, and otherwise asks for the id. Tests pass custom scripts, for example a model that always requests another customer's order.

## DevUI (Development only)

Run the API (or the AppHost, where the `support-api-dotnet` resource shows a **DevUI** link) and open `http://localhost:5180/devui/`. DevUI lists:
- **`support-routing` (workflow):** chat through the whole pipeline, exactly like an API turn (triage, the routing switch, one specialist). Each reply starts with the routing result, for example `[route: billing | reply: answer]`. DevUI can also show the workflow graph.
- **`orders`, `billing`, `technical` (agents):** talk to one specialist directly, skipping triage. This is useful for checking a tool on its own.

DevUI and its OpenAI-compatible `/v1/responses` and `/v1/entities` endpoints are mapped only in Development. They use the same agent instances and the same executors as the API, so the same ownership checks apply. The DevUI workflow differs only at its two ends:
- a `chat-input` entrance that speaks MAF's chat protocol and forwards the text to triage;
- a reply exit that emits an agent response update.

DevUI's built-in guard admits only loopback callers with a localhost `Host` header. It applies to any path containing the segments `/v1/responses` or `/v1/conversations`, **which includes our `/api/v1/conversations` routes in Development**. Local use is unaffected. A proxy that forwards a non-localhost `Host` (relevant to L23) must preserve a loopback host in Development.

## Live smoke check (opt-in, costs a small amount)

```bash
export ANTHROPIC_API_KEY=...   # never commit it
dotnet test --solution maf-support-lab.slnx -- --filter-trait "Category=Live" --explicit only
```

These are three turns against `claude-haiku-5-5`, one per domain (orders, billing S05, technical S07). Each is about three model calls: triage, then the specialist and its tool round trip.

They never run in CI or a plain `dotnet test`.

## Who does what

| Concern | Owner | Why |
| --- | --- | --- |
| Identity, ownership, eligibility, approval enforcement, idempotency, operation state | Application handlers (plain C#, `Modules/*/Application`) | Must be deterministic, testable, and impossible for the model to bypass. |
| HTTP contract, validation, error mapping, streaming | Presentation endpoints (`Modules/*/Presentation`, `Common/Presentation`) | Implements `contracts/` exactly. |
| Understanding the request, choosing a route, wording the reply, deciding which read tool to call | Agents and workflow (MAF, from L05/L07) | Language understanding is the model's job; its output is validated before use. |
| Startup, health, telemetry, local orchestration | Host, ServiceDefaults, AppHost | Infrastructure, not business logic. |

Agents reach application handlers through tools. Tools receive the principal from trusted server context, never as a model argument.
