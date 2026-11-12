# Runbook: set up, run, check

How to get both backends running locally, exercise them, and run the same checks as CI. Fake model mode is the default everywhere: it needs no key and makes no billable call.

## Prerequisites

Versions are pinned; the [version baseline](decisions/0001-version-baseline.md) records why.

| Tool | Version | Pinned in |
| --- | --- | --- |
| .NET SDK | 11.0.100 (patch roll-forward only) | `global.json` |
| Python | 3.15.2 | CI and the [version baseline](decisions/0001-version-baseline.md) |
| uv | 0.13.7 | CI |
| Aspire | AppHost SDK 13.6.5; CLI 13.6.x, optional | `apphost/Support.AppHost.csproj` |
| `aieval` (Microsoft.Extensions.AI.Evaluation.Console) | 10.11.0, local tool | `.config/dotnet-tools.json` |
| Node.js and pnpm (the demo frontend; the AppHost needs them on `PATH`) | Node 26, pnpm 12.15.0 | `.node-version`, `frontend/package.json` |

The main application packages (versions as locked; the full list and the reasoning are in [0001](decisions/0001-version-baseline.md)):

| Package | .NET | Python |
| --- | --- | --- |
| Microsoft Agent Framework | `Microsoft.Agents.AI` 1.28.0 (+ Workflows, Anthropic) | `agent-framework-core` 1.25.0 (+ `-anthropic`, `-devui` 1.0.0b261106) |
| Web host | ASP.NET Core 11 | FastAPI 0.162.3, Uvicorn 0.54.0 |
| Model SDK | `Anthropic` 13.8.0 | `anthropic` 1.17.0 |
| Storage | `Microsoft.Data.Sqlite` 11.0.0 (pooled) | `sqlite3` (standard library) |
| Telemetry | OpenTelemetry 1.21 | OpenTelemetry (locked in `uv.lock`) |
| Evaluation (runner only) | `Microsoft.Extensions.AI.Evaluation` + `.Quality` + `.Reporting` 10.11.0 | `agent-framework-core` 1.25.0 |

NuGet packages are locked (`packages.lock.json`), Python is locked (`src/python/uv.lock`, `evaluations/python/uv.lock`), and so is the frontend (`frontend/pnpm-lock.yaml`). Docker is needed only for the frontend's screenshot tests.

## Run both backends under Aspire

```bash
dotnet run --project apphost        # or: aspire start --apphost apphost/Support.AppHost.csproj
```

Open the dashboard link that is printed. It shows `support-api-dotnet` (http://localhost:5180, with a DevUI link), `support-api-python`, `support-devui-python` and `web`, the demo frontend (Aspire runs `pnpm install --frozen-lockfile` on every start, then the Next.js dev server, which talks to the .NET backend), with their health, logs, traces and metrics. To run one backend alone, see the [.NET README](../src/dotnet/README.md#run) or the [Python README](../src/python/README.md#run-it-on-its-own-no-aspire-no-secrets).

## Exercise the API

These commands were checked against the .NET backend in fake mode on 2026-11-12. The Python backend answers the same contract (when run alone, it listens on port 5280).

```bash
B=localhost:5180/api/v1
C=$(curl -s -X POST $B/conversations -H 'content-type: application/json' -d '{}' | jq -r .conversation_id)

# A read: routed to the orders agent, answered from the fixtures
curl -s -X POST $B/conversations/$C/turns -H 'content-type: application/json' \
  -d '{"client_turn_id":"'$(uuidgen)'","message":"Where is my order ORD-1002?"}' | jq '{outcome, route, reply, trace_id}'

# A write: the turn pauses for approval (nothing has changed yet)
R=$(curl -s -X POST $B/conversations/$C/turns -H 'content-type: application/json' \
  -d '{"client_turn_id":"'$(uuidgen)'","message":"Please cancel order ORD-1003."}')
echo "$R" | jq '{outcome, pending_approval}'          # outcome: awaiting_approval

# Approve it: the operation runs once, and the resumed turn reports the fact
A=$(echo "$R" | jq -r .pending_approval.approval_id); O=$(echo "$R" | jq -r .pending_approval.operation_id)
curl -s -X POST $B/conversations/$C/approvals/$A -H 'content-type: application/json' \
  -d '{"operation_id":"'$O'","decision":"approve"}' | jq '{outcome, reply}'   # "Done: order ORD-1003 is cancelled."
curl -s $B/operations/$O | jq '{status, result}'       # status: committed
```

`client_turn_id` makes a turn idempotent: re-sending one returns the original turn. Add `-H 'accept: text/event-stream'` to stream a turn. The full flow is in [API, approval, and state flow](api-flow.md); tracing a turn is in [observability](observability.md#run-it-and-look).

## Live mode (opt-in, billed)

| Backend | Turn it on |
| --- | --- |
| .NET | Key in user-secrets (`ModelProvider:Anthropic:ApiKey`) or `ANTHROPIC_API_KEY`, then `ModelProvider__Mode=Live` ([details](../src/dotnet/README.md#model-mode)) |
| Python | `MODEL_PROVIDER__MODE=live`, `ANTHROPIC_API_KEY`, and `MODEL_PROVIDER__ANTHROPIC__MODEL` ([details](../src/python/README.md#configuration)) |

The model is `claude-haiku-5-5`. In Live mode with a missing key or model, a backend refuses to start and names the missing setting, never its value. Each turn is capped in model calls ([budgets](observability.md#failures-retries-and-budgets)).

## The chat page against live Claude (opt-in, billed)

The chat page's end-to-end flows also run against live Claude Haiku. They start the backend themselves, in live mode, with a fresh store:

```bash
cd frontend && pnpm build
BACKEND=dotnet MODEL=live pnpm e2e:flows                            # .NET reads its key from user secrets
ANTHROPIC_API_KEY=... BACKEND=python MODEL=live pnpm e2e:flows      # Python reads ANTHROPIC_API_KEY
BACKEND=dotnet MODEL=live DEMO=1 RECORD=1 pnpm e2e:flows demo.spec.ts   # re-record the README's video (test-results/)
```

A full live run is roughly 50 model calls per backend, a few cents on `claude-haiku-5-5`.

## Checks (as CI runs them)

```bash
python3 scripts/check_docs_links.py && python3 -m unittest discover -s scripts/tests   # every relative link, anchor and image
dotnet build maf-support-lab.slnx -warnaserror && dotnet test --solution maf-support-lab.slnx
(cd src/python && uv sync --locked && uv run ruff check . && uv run ruff format --check . && uv run pyright && uv run pytest)
(cd evaluations/python && uv run --locked pytest)
(cd frontend && pnpm install --frozen-lockfile && pnpm peers check && pnpm lint && pnpm format:check && pnpm typecheck && pnpm test && pnpm build && pnpm e2e:docker)
(cd frontend && BACKEND=dotnet pnpm e2e:flows && BACKEND=python pnpm e2e:flows)   # the chat page against each backend (after pnpm build)
```

The cross-backend probes, the evaluations and the report are in [testing](testing.md) and [evaluation reports](evaluations/reports.md#reproduce). Opt-in live tests are `dotnet test … -- --filter-trait "Category=Live" --explicit only` and `uv run pytest -m live`. The model-judged quality lane is `quality --live` ([quality](evaluations/quality.md)).

## Common problems

| Symptom | Cause and fix |
| --- | --- |
| The .NET API calls Claude when you expected fake mode | Your user-secrets select Live. Set `ModelProvider__Mode=Fake` for that run; it overrides user-secrets. |
| `dotnet restore` fails with NU1004 (lock file drift) | A version in `Directory.Packages.props` changed. Run `dotnet restore` and commit the updated `packages.lock.json`. |
| `uv sync --locked` fails | `pyproject.toml` and `uv.lock` disagree. Run `uv lock` deliberately, review, and commit. |
| `409 turn_in_progress` | That conversation already has a running turn: wait, or `POST …/turns/{id}/cancel`. |
| `409 client_turn_id_reused` | The same `client_turn_id` was sent with a different message. Generate a new id per message. |
| `410 approval_expired` | Approvals expire after 15 minutes. Ask again. |
| A stream dropped before `turn.final` | Read `GET …/turns/{turn_id}`, then `GET /api/v1/operations/{id}`; they are authoritative. |
| Starting the API fails: the port is in use | A previous run is still listening on 5180 or 5280. Stop it. |
| `curl` to the Python backend fails under Aspire | Aspire serves it over **https** on a dynamic port. Take the URL from the dashboard and add `-k` for the local development certificate. |
| `git push` is rejected with a secret-scanning message | Push protection found a credential in the commit. Remove it from the commit (don't just delete it in a later commit), rotate the key, and push again. |
| A live run fails at startup with a missing setting | Live mode needs the key and the model. The error names the setting, never its value; see [Live mode](#live-mode-opt-in-billed). |
| `aieval` won't start on a machine with only .NET 11 | The tool targets .NET 8–10, and `rollForward: true` in the manifest allows .NET 11. Run `dotnet tool restore` first. |

Never paste the output of `aspire describe` with environment variables: it includes the dashboard's OTLP API key.
