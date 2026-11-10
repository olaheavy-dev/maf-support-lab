# .NET support API

The C# implementation of the support system, run locally through the Aspire AppHost in `apphost/`.

| Project | Purpose |
| --- | --- |
| `Support.Api` | ASP.NET Core host: configuration, model mode, identity, health. Agents arrive in L05. |
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

## Who does what

| Concern | Owner | Why |
| --- | --- | --- |
| Identity, ownership, eligibility, approval enforcement, idempotency, operation state | Application services (plain C#) | Must be deterministic, testable, and impossible for the model to bypass. |
| HTTP contract, validation, error mapping, streaming | API endpoints (L05, L12) | Implements `contracts/` exactly. |
| Understanding the request, choosing a route, wording the reply, deciding which read tool to call | Agents and workflow (MAF, from L05/L07) | Language understanding is the model's job; its output is validated before use. |
| Startup, health, telemetry, local orchestration | Host, ServiceDefaults, AppHost | Infrastructure, not business logic. |

Agents call services through tools. Tools receive the principal from trusted server context, never as a model argument.
