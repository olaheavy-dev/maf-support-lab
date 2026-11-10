# Python backend (`support_agent`)

The VoltCart support backend in Python. It serves the same version-1 contract as the .NET backend (`contracts/README.md`), built with Microsoft Agent Framework for Python. Its logic is its own: nothing here imports .NET code for telemetry, identity, or authorization. A lint rule and a test enforce that.

Status (L18): the host plus read-only agents.
- **Read-only agents:** triage plus the orders, billing and technical specialists.
- **The routing workflow:** `support-routing`.
- **The version-1 contract API** for conversations and turns (JSON).
- **Python DevUI** (development only).

There are no tools that change anything. Writes arrive with the approval gate (L37); sessions arrive in L19.

## How it fits together

| Module | What it does |
| --- | --- |
| `settings.py`, `identity.py`, `dependencies.py` | Validated configuration; the server-owned principal, pinned per turn (`identity.pinned`) |
| `fixtures.py`, `domain.py` | The shared synthetic fixtures (`fixtures/data`), as validated Pydantic records |
| `services.py` | Owner-checked, read-only lookups. Another customer's record looks exactly like a missing one. |
| `tools.py` | The agents' tools (`@tool`). Ids only; never a customer id. Each records its outcome in `activity.py`. |
| `agents.py` | Triage plus three specialists, each with only its own tools (ported .NET instructions) |
| `routing.py`, `contract.py` | Triage output validated against the **full** contract schema, then deterministic routing rules |
| `workflow.py` | MAF workflow: triage → switch → ask-for-order-id / write-not-available / specialist / unrouted → reply |
| `api.py`, `conversations.py` | The contract API: schema-validated requests, the same 404 for foreign or missing, `client_turn_id` replay, `failed` results |
| `fake_model.py`, `model_provider.py` | A deterministic fake model on MAF's real chat-client layers; Claude in live mode |
| `devui.py` | Development-only DevUI process over the same agents and services |

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
| `ANTHROPIC_API_KEY` | Required in live mode. Secret: environment or Aspire parameter only; never committed, logged, or returned. |

## Identity

The app learns the customer only through `get_principal` → `PrincipalSource`.
- Headers, query strings, bodies, and model output are never consulted.
- Tests run as another customer through `app.dependency_overrides[get_principal_source]`, a server-side injection point no client can reach.

## Clients and credentials: who cleans up

The FastAPI **lifespan owns every long-lived resource**. A client (later, the Anthropic client and the database) is created once, registered with `app.state.resources.own(name, close)`, and closed exactly once, newest first, at shutdown. No request creates its own long-lived client. The API key lives only in validated settings (`SecretStr`), and is passed to the client that needs it.

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
