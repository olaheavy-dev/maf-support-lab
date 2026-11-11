# Evaluations

Evaluation of the two support backends, kept apart from the application: nothing here is referenced by `src/`, and no backend manifest gains an evaluation dependency. It started in L29 (datasets, result contracts, project shells, and the data-to-result flow). L16 grows the runner, L30/L31 add Microsoft's native .NET and Python evaluators, L32 adds budgeted model-based scoring, and L33 adds reports.

| Path | What it holds |
| --- | --- |
| `schemas/evaluation-dataset.schema.json` | What a dataset is: versioned cases with steps and expected **observable facts**. |
| `schemas/evaluation-run.schema.json` | What a run produces: run, backend, configuration, dataset, scorer and runner identity, plus per-case step evidence, checks, latency and usage availability. |
| `datasets/*.json` | Five versioned datasets (27 cases): `support-success`, `support-ambiguity`, `support-denial`, `support-failure`, `support-adversarial`. |
| `tools/build_datasets.py` | Generates the datasets from `fixtures/scenarios` plus evaluation-only cases. Rerun it after changing either, then commit the output. |
| `dotnet/Support.Evaluations/` | The runner (.NET console app): `validate`, and `run` against any backend URL. |
| `dotnet/Support.Evaluations.Tests/` | Dataset and result contract tests, and the runner over a fake backend. |
| `python/` | The Python evaluation package (its own locked uv project): the same contract checks; native evaluations from L31. |
| `tools/run_against_backends.py` | The end-to-end flow against both real backends in fake mode (CI's `cross-backend` job). |
| `results/` | Generated run documents. Git ignores them; CI uploads them as the `evaluation-results` artifact. |

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

- **Customers:** a backend process serves one development customer, so cases that act as another customer need that customer's process too, on the same store. `--principal CUST-1002=URL` provides it. A case whose customer has no process is skipped, with the reason.
- **Writes are never replayed on a shared store.** Write cases run only with `--allow-writes`, which promises a fresh, isolated store. Otherwise they are skipped with "needs an isolated store". L16 adds a fresh store per write case.
- **Usage** is recorded as unknown (`available: false`): the contract doesn't expose token counts, and unknown is never reported as zero.
- **To reproduce a result** you need `run`: backend, source commit, model mode and id, dataset id and version, splits, scorer and runner versions, and configuration (writes allowed, customers, fixture version).

```bash
# Validate the datasets
dotnet run --project evaluations/dotnet/Support.Evaluations -- validate

# Run against backends you started yourself (fake mode)
dotnet run --project evaluations/dotnet/Support.Evaluations -- run --backend python \
  --principal CUST-1001=http://127.0.0.1:5280 --split development,heldout

# Or the whole flow against both backends, as CI does (needs contracts/requirements.txt, the .NET SDK and uv)
python evaluations/tools/run_against_backends.py --dotnet-build
```

The runner's exit code is 0 when every selected case passed, 1 if any failed or errored, and 2 for invalid input.
