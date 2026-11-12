# 0002 - Code layout: modules and layers as folders

- Status: Accepted (2026-11-10). The owner chose each option below.
- Applies to: `src/dotnet/Support.Api` now. The Python backend follows the same module and layer names in Python style.

## Context

After the first agent, the API had folders by topic (`Agents/`, `Contract/`, `Orders/`, `Configuration/`…) but no layers. One 193-line endpoint file mixed four jobs:

- HTTP parsing and validation;
- the idempotent turn flow and running the agent;
- the `reply.kind` business rule;
- building the response.

The in-memory store (future database code) sat beside the HTTP models. Nothing showed what touches storage, what is HTTP, or where a new rule belongs.

The owner's SabiTutor.Platform uses a modular monolith: each module has Domain, Application, Infrastructure, and Presentation layers, plus CQRS handlers and architecture tests. This lab adopts that shape at a size that fits one small API.

## Decision

**One project, folders as layers.** Namespaces follow folders.

```text
Support.Api/
├─ Program.cs            composition root: AddXModule()/MapXModule() only
├─ Common/
│  ├─ Domain/            Result, Error, ErrorType
│  ├─ Application/       ICommand/IQuery and their handler interfaces, TraceIds
│  └─ Presentation/      contract JSON options, problem responses, Result.Match
└─ Modules/<X>/          Conversations, Orders, Identity, Agents
   ├─ Domain/            entities and rules with no I/O
   ├─ Application/       one command or query + sealed handler per use case; interfaces for storage
   ├─ Infrastructure/    storage, fixtures, model provider: anything that does I/O
   ├─ Presentation/      HTTP endpoints and agent tools: translate in, call a handler, translate out
   └─ <X>Module.cs       the module's DI registration and endpoint mapping
```

**Dependencies point inwards:**

| Layer | Must not use |
| --- | --- |
| Domain | Application, Infrastructure, Presentation |
| Application | Infrastructure, Presentation |
| Presentation | Infrastructure |
| Infrastructure | (may use all; it implements Application's interfaces and wires tools into agents) |

The rules apply across modules. For example, Conversations' Application may use Agents' Application, but never any module's Infrastructure.

**Handlers:** SabiTutor's pattern:
- `ICommand<T>` and `IQuery<T>`, with `ICommandHandler`/`IQueryHandler` returning `Result<T>`;
- expected failures (not found, conflict, invalid order id) are `Error` values, not exceptions;
- `Error.Code` is the contract's error code, so `ApiResults.Problem` maps it to the contract problem;
- endpoints and tools call `Match`.

**Agents is the shared AI runtime; feature modules plug into it.**
- The Agents module holds the model provider, the `SupportAgents` collection, `TurnActivity`, and DevUI.
- Each feature module builds its own agent and tools, and registers a `SupportAgent(name, agent)`. Orders, Billing and Technical all do this.
- Agents never refers back to a feature module. This removed an Agents↔Orders loop found in review.

**Agent tools are Presentation.** A tool is the model's endpoint. `get_order_status` validates nothing itself: it sends a query and turns the `Result` into what the model sees. Ownership stays in the handler (AGENTS.md: authorization and idempotency live in application/tool code).

**Enforcement:** one compiled project can't stop a layer reaching into another, so ArchUnitNET (`TngTech.ArchUnitNET.xUnitV3` 0.13.5, Apache-2.0, released 2026-10-26) checks these rules in `Support.Api.Tests/Architecture`:
- the layer table above;
- handlers are sealed, named `…Handler`, and live in Application;
- commands and queries are sealed and live in Application;
- every type is in a layer folder, apart from `<X>Module` wiring classes;
- every expected layer has types, so no rule passes by matching nothing;
- the Agents module depends on no other module.

`RuleSelfTests` runs each production rule over deliberately broken fixtures and expects exactly the offending types.

## Deliberately not adopted from SabiTutor

| SabiTutor | Here | Why |
| --- | --- | --- |
| Six projects per module | Folders | Same separation with less ceremony. The architecture tests replace the compiler's project-reference check. |
| PublicApi and IntegrationEvents projects | None | Modules call each other's Application layer directly. No events exist yet. |
| Full module isolation rules | Only "Agents depends on no module" | Feature modules legitimately share identity and the agent runtime. Fuller isolation is the stretch exercise. |
| Carter | Plain minimal-API `Map…` methods | No need for another package. |
| `apps/api/` layout | `src/dotnet/` kept | About 30 tickets reference the existing paths, and the Python backend sits beside it. |
| Non-generic `ICommand`, `Result` without value | Omitted | No use case needs them yet. Add them when one does. |

## Consequences

- New behaviour has an obvious home. Examples: telemetry went into Infrastructure and host defaults; triage added Application handlers and an Agents change; durable storage replaced `InMemoryConversationStore` only.
- A handler is easy to test without HTTP, and an endpoint stays a translation layer.
- **Known gap:** the dependency rules see real uses, such as fields, constructor parameters, `new`, and method calls. They do **not** see a bare `typeof(...)` inside a method body (checked by mutation). Code review covers that rare case.
- A rule can be broken by placing a type in the wrong folder *and* namespace together. The placement rule catches types outside layers, but not a type deliberately put in the wrong layer. That is a review concern.
