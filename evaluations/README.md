# Evaluations

Evaluation of the two support backends, kept apart from the application: nothing here is referenced by `src/`, and no backend manifest gains an evaluation dependency. It started in L29 (datasets, result contracts, project shells, and the data-to-result flow). L16 made the runner launch the backends itself, so every case runs (fresh stores, restarts, cancellation), and added the baseline. L30/L31 add Microsoft's native .NET and Python evaluators, L32 adds budgeted model-based scoring, and L33 adds reports.

| Path | What it holds |
| --- | --- |
| `schemas/evaluation-dataset.schema.json` | What a dataset is: versioned cases with steps and expected **observable facts**. |
| `schemas/evaluation-run.schema.json` | What a run produces: run, backend, configuration, dataset, scorer and runner identity, plus per-case step evidence, checks, latency and usage availability. |
| `datasets/*.json` | Five versioned datasets (31 cases, schema 1.1): `support-success`, `support-ambiguity`, `support-denial`, `support-failure`, `support-adversarial`. |
| `tools/build_datasets.py` | Generates the datasets from `fixtures/scenarios` plus evaluation-only cases. Rerun it after changing either, then commit the output. |
| `dotnet/Support.Evaluations/` | The runner (.NET console app): `validate`; `run`, either launching the backends itself (`--launch`) or against URLs you give it (`--principal`); and `compare`, which writes the baseline. |
| `recordings/*.json`, `recordings/python/*.json` | Captured conversations for native evaluation (L30 .NET, L31 Python), schema `evaluation-capture`: the query, the agent's messages with tool calls, results and paused approvals, the tools it had, and its case. Re-record with `native record` after a dataset version changes. |
| `dotnet/Support.Evaluations.Tests/` | Dataset and result contract tests, and the runner over a fake backend. |
| `python/` | The Python evaluation package (its own locked uv project): the same contract checks; native evaluations from L31. |
| `results/` | Generated run documents. Git ignores them; CI uploads them as the `evaluation-results` artifact. The curated baseline is [docs/evaluations/baseline.md](../docs/evaluations/baseline.md). |

## Native .NET evaluation (L30)

Microsoft Agent Framework 1.28.0 has local evaluation built in: `EvalItem`, `ExpectedToolCall`, `EvalChecks`, `FunctionEvaluator`, `LocalEvaluator`, and `agent.EvaluateAsync(...)`, all on the Microsoft.Extensions.AI.Evaluation abstractions. The runner uses it in two ways, with no judge model and no credentials:

- **`native record`:** MAF runs a fake support agent (a scripted model, plus tools with the .NET backend's names and parameters over the shared fixtures, with writes approval-required) on each native case's query. It scores the result and saves each conversation as a capture.
- **`native evaluate`:** scores the saved captures with MAF's recorded-response overload, `EvaluateAsync(responses, queries, …)`, which never re-runs the agent. The run fails if the model was called even once.

**The checks** (`Native/SupportChecks.cs`):

| Check | What its evidence can show |
| --- | --- |
| `tool_call_args_match` (MAF's built-in) | The expected tool, with the expected arguments (subset match) |
| `writes_wait_for_approval` | No write tool has a result in the turn, so it paused |
| `no_unexpected_writes` | No write the case didn't expect was asked for |
| `record_ids_are_grounded` | Every record id in the reply came from the query or a tool result; format examples like `ORD-1234` excepted |
| `cites_found_record` | A record a lookup found is named in the reply |

None of them can show that a write was *authorized*. That's the backend's gate, checked through its API by the behaviour runner (L16), and the native scores sit alongside those assertions, never instead of them. Expected arguments are written once in canonical snake_case in the datasets, and the .NET adapter maps them to the tools' C# names (`order_id` → `orderId`).

## Native Python evaluation (L31)

The same evaluation with MAF Python 1.25.0 (`evaluations/python/support_evaluations/native.py`), run with `python -m support_evaluations native …`:
- **`record`:** `evaluate_agent(agent=…, queries=…)` runs a fake support agent (a scripted model on MAF's real tool loop, with the Python backend's snake_case tools over the fixtures and approval-required writes). The conversations are saved to `recordings/python/`.
- **`evaluate`:** `evaluate_agent(responses=…, queries=…)` skips running the agent. It fails on any model call, a stale capture, or a native case with no recording (or more than one), and writes run documents (`backend: python`, schema 1.2) with evidence links.
- **`workflow`:** `evaluate_workflow` runs a one-agent workflow and scores the overall output and each agent.

The checks are the same as .NET's: MAF's `tool_call_args_match`, plus the four domain checks written with `@evaluator`. Expected arguments need no mapping, because the canonical names are the Python tools' own.

## Business fixtures, datasets, scorers, results

They are four separate things:

1. **Fixtures** (`fixtures/`): the synthetic business data both backends load, such as orders, invoices and policies. Datasets never change them.
2. **Datasets** (`evaluations/datasets/`): what to ask, and which facts must be true afterwards. Each case has:
   - a `split`: `development` cases may be used while changing prompts or code; `heldout` cases are only run to measure, never tuned against;
   - a `reset`: `fresh_conversation` for read-only cases, `fresh_store` for cases that write;
   - optional `context`: the fixture records the answer is grounded in, checked to exist, for later groundedness scoring.

   Each dataset also has a `live_run` budget for an explicitly invoked live run. The `failure` dataset uses fake-model markers, so it is not live-eligible.
3. **Scorer** (`contract-facts` 1.0.0): one named check per expected fact:
   - HTTP status, problem code, outcome, error code;
   - route, reply kind, operation kind and status;
   - tools seen in the stream's `tool_call` progress events;
   - same turn or operation on a replay.

   `mentions` is a content check on the reply text, never evidence of what happened. `must_not_mention` scans the whole result for leaked data.
4. **Results** (`evaluations/results/`): one run document per backend and dataset, validated against its schema by the runner before it's written.

## The flow

```text
dataset (validated) ──▶ runner ──HTTP──▶ backend (fake mode)
                          │   new conversation per case; each step streamed (SSE) so tool calls are visible
                          ▼
                 checks (contract facts) ──▶ run document (validated) ──▶ evaluations/results/<backend>-<dataset>.json
```

- **Launch mode** (`--launch`, L16): the runner starts the backend itself, in fake mode, with one process per customer (a backend serves one development customer per process) on one SQLite file.
  - Read-only cases share one store.
  - **Every write case gets a fresh store and fresh processes**, so a write never repeats on shared state.
  - A `restart` step kills the processes and starts them again on the same file (a realistic crash).
  - Process trees are killed whole, because `dotnet run` and `uv run` start children.
- **Endpoint mode** (`--principal CUST-1001=URL`): backends you started yourself.
  - Write cases run only with `--allow-writes`, which promises a fresh, isolated store; otherwise they are skipped with the reason.
  - Restart cases are skipped, because the runner doesn't own the processes.
  - A case whose customer has no process is skipped.
- **Steps:** `send` (optionally `cancel_after_start`: the runner cancels as soon as the stream names the turn), `resend_previous`, `decide`, `send_raw`, `restart`, and `get_operation`. `get_operation` reads the operation record: the authoritative state, checked with an `operation_result` subset. A step that builds on a failed step makes the case *failed*, not a runner error.
- **Latency** is API time: the sum of a case's request steps. Process startup and restarts don't count.
- **Usage** is recorded as unknown (`available: false`): the contract doesn't expose token counts, and unknown is never reported as zero.
- **To reproduce a result** you need `run`: backend, source commit, model mode and id, dataset id and version, splits, scorer and runner versions, and configuration (writes allowed, customers, fixture version).

```bash
# Validate the datasets
dotnet run --project evaluations/dotnet/Support.Evaluations -- validate

# Every dataset on a backend the runner starts itself (build the API in Release first), as CI does
dotnet build src/dotnet/Support.Api -c Release
dotnet run --project evaluations/dotnet/Support.Evaluations -- run --backend dotnet --launch --split development,heldout

# Or against backends you started yourself (fake mode)
dotnet run --project evaluations/dotnet/Support.Evaluations -- run --backend python \
  --principal CUST-1001=http://127.0.0.1:5280 --split development,heldout

# The baseline and comparison from the run documents
dotnet run --project evaluations/dotnet/Support.Evaluations -- compare --results evaluations/results --out docs/evaluations/baseline.md
```

The runner's exit code is 0 when every selected case passed, 1 if any failed or errored, and 2 for invalid input.
