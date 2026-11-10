# .NET support API

The C# implementation of the support system, run locally through the Aspire AppHost in `apphost/`.

| Project | Purpose |
| --- | --- |
| `Support.Api` | ASP.NET Core host: contract API, orders agent and tool, fixtures, model mode, identity, health, Development-only DevUI. |
| `Support.ServiceDefaults` | Shared Aspire defaults: OpenTelemetry export, `/health` and `/alive`, service discovery, HTTP resilience. |
| `Support.Api.Tests` | Integration tests through the ASP.NET Core test host (xUnit v3, Microsoft Testing Platform). |
| `../../apphost` | Aspire AppHost: starts the API, shows it in the dashboard, and watches `/health`. |

Versions are pinned in `global.json` (SDK) and `Directory.Packages.props` (packages) at the repository root, per [the version baseline](../../docs/decisions/0001-version-baseline.md).

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

- **`orders` agent** (`Agents/OrdersAgentFactory.cs`): built once and shared by the API and DevUI. It has a single tool.
- **`get_order_status` tool** (`Orders/OrderStatusTool.cs`):
  - The model supplies only the order id.
  - `OrderService` returns the order only if it belongs to the current principal; anything else looks exactly like a missing order.
  - The tool records what it found. The API sets `reply.kind` (`answer`, `not_found`, `clarification`) from those facts, not from the model's wording.
- **Fake model** (`Model/FakeChatClient.cs`): used in Fake mode and tests. It requests the tool when the message contains an order id, answers from the tool result, and otherwise asks for the id. Tests pass custom scripts, for example a model that always requests another customer's order.

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
| Identity, ownership, eligibility, approval enforcement, idempotency, operation state | Application services (plain C#) | Must be deterministic, testable, and impossible for the model to bypass. |
| HTTP contract, validation, error mapping, streaming | API endpoints (L05, L12) | Implements `contracts/` exactly. |
| Understanding the request, choosing a route, wording the reply, deciding which read tool to call | Agents and workflow (MAF, from L05/L07) | Language understanding is the model's job; its output is validated before use. |
| Startup, health, telemetry, local orchestration | Host, ServiceDefaults, AppHost | Infrastructure, not business logic. |

Agents call services through tools. Tools receive the principal from trusted server context, never as a model argument.
