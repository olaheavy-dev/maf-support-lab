# Python backend (`support_agent`)

The VoltCart support backend in Python. It serves the same version-1 contract as the .NET backend (`contracts/README.md`), built with Microsoft Agent Framework for Python. Its logic is its own: nothing here imports .NET code for telemetry, identity, or authorization. A lint rule and a test enforce that.

Status (L17): the host shell only.
- Validated configuration; fake mode needs no secrets.
- The development identity boundary.
- `/health` and `/alive`, plus `/dev/whoami` in development.
- Resource ownership (`Resources`), and Aspire integration.

Agents, tools, and the contract API arrive in L18 and later.

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

## Checks (the same as CI)

```bash
uv sync --locked          # the lock file must match pyproject.toml
uv run ruff check .       # lint (includes the "no .NET imports" rule)
uv run ruff format --check .
uv run pyright            # strict type checking
uv run pytest
```

`pyproject.toml` declares what the project needs (direct dependencies, with exact pins). `uv.lock` records the full resolved tree, transitive packages included, with hashes. `--locked` fails if the two disagree, so CI and every machine install exactly the same packages.
