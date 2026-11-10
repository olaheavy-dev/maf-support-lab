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
│  ├─ Application/            ICommand/IQuery + handler interfaces, TraceIds
│  └─ Presentation/           ContractJson, ApiResults (every problem response), Result.Match
└─ Modules/
   ├─ Conversations/          the contract API
   │  ├─ Domain/              Conversation (idempotent turn reservation), Turn, ReplyKind
   │  ├─ Application/         CreateConversation, GetConversation, SubmitTurn, GetTurn handlers; IConversationStore
   │  ├─ Infrastructure/      InMemoryConversationStore  ← storage lives here (durable in L11)
   │  └─ Presentation/        ConversationEndpoints (thin), ConversationRequests (contract validation), contract shapes
   ├─ Orders/
   │  ├─ Domain/              Order, OrderId
   │  ├─ Application/         GetOrderStatus query + handler (ownership check); IOrderRepository
   │  ├─ Infrastructure/      FixtureOrderRepository (fixtures/data/orders.json), OrdersAgentFactory (the orders agent)
   │  └─ Presentation/        OrderStatusTool: the model's "endpoint"
   ├─ Identity/               SupportPrincipal, IPrincipalSource, DevelopmentPrincipalSource, /dev/whoami
   └─ Agents/                 shared AI runtime, depends on no other module: SupportAgents and TurnActivity (Application);
                              model provider and FakeChatClient (Infrastructure); DevUI (Presentation)
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
- `GET /api/v1/conversations/{id}/turns/{turn_id}`.

The rules:
- Requests with unknown fields (such as `customer_id`) get `400 invalid_request`.
- Someone else's conversation or turn returns `404 not_found`.
- Re-sending a `client_turn_id` returns the original turn; reusing it with a different message returns `409`.

Not yet implemented: triage (L07), the one-turn-at-a-time rule and conversation memory (L09), writes and approvals (L10), and streaming (L12). Until L07 every turn routes to `orders`.

```bash
curl -s -X POST localhost:5180/api/v1/conversations -H 'content-type: application/json' -d '{}'
curl -s -X POST localhost:5180/api/v1/conversations/<conversation_id>/turns -H 'content-type: application/json' \
  -d '{"client_turn_id":"'$(uuidgen)'","message":"Where is my order ORD-1002?"}'
```

## Agents, tools, and the model boundary

- **`orders` agent** (`Modules/Orders/Infrastructure/OrdersAgentFactory.cs`, registered by `OrdersModule` as a `SupportAgent`): built once and shared by the API and DevUI. It has a single tool.
- **`get_order_status` tool** (`Modules/Orders/Presentation/OrderStatusTool.cs`):
  - The model supplies only the order id.
  - The tool sends a `GetOrderStatusQuery`. Its handler returns the order only if it belongs to the current principal; anything else looks exactly like a missing order.
  - The tool records what it found. The API sets `reply.kind` (`answer`, `not_found`, `clarification`) from those facts, not from the model's wording.
- **Fake model** (`Modules/Agents/Infrastructure/ModelProvider/FakeChatClient.cs`): used in Fake mode and tests. It requests the tool when the message contains an order id, answers from the tool result, and otherwise asks for the id. Tests pass custom scripts, for example a model that always requests another customer's order.

## DevUI (Development only)

Run the API and open `http://localhost:5180/devui/` to chat with the `orders` agent and inspect its tool calls. DevUI and its OpenAI-compatible `/v1/responses` and `/v1/entities` endpoints are mapped only in Development, and they use the same agent instance as the API.

DevUI's built-in guard admits only loopback callers with a localhost `Host` header. It applies to any path containing the segments `/v1/responses` or `/v1/conversations`, **which includes our `/api/v1/conversations` routes in Development**. Local use is unaffected. A proxy that forwards a non-localhost `Host` (relevant to L23) must preserve a loopback host in Development.

## Live smoke check (opt-in, costs a small amount)

```bash
export ANTHROPIC_API_KEY=...   # never commit it
dotnet test --solution maf-support-lab.slnx -- --filter-trait "Category=Live" --explicit only
```

This is one turn against `claude-haiku-5-5` (two model calls including the tool round trip). It never runs in CI or a plain `dotnet test`.

## Who does what

| Concern | Owner | Why |
| --- | --- | --- |
| Identity, ownership, eligibility, approval enforcement, idempotency, operation state | Application handlers (plain C#, `Modules/*/Application`) | Must be deterministic, testable, and impossible for the model to bypass. |
| HTTP contract, validation, error mapping, streaming | Presentation endpoints (`Modules/*/Presentation`, `Common/Presentation`) | Implements `contracts/` exactly. |
| Understanding the request, choosing a route, wording the reply, deciding which read tool to call | Agents and workflow (MAF, from L05/L07) | Language understanding is the model's job; its output is validated before use. |
| Startup, health, telemetry, local orchestration | Host, ServiceDefaults, AppHost | Infrastructure, not business logic. |

Agents reach application handlers through tools. Tools receive the principal from trusted server context, never as a model argument.
