# Evaluation reports and CI lanes (L33)

Every evaluation result can be rendered as a Microsoft.Extensions.AI.Evaluation report (HTML for people, JSON for machines). Rendering reads result files only: it starts no backend, agent or judge, and needs no credentials. Generated outputs live in `evaluations/results/`, which Git ignores. This page holds the curated summary.

## Lanes

| Lane | Where | When | Gate? | Model calls |
| --- | --- | --- | --- | --- |
| Deterministic behaviour (both backends, all datasets, launch mode) | `repository-checks` → `cross-backend` | every push and PR | **Required.** Any failed or errored case exits 1 and fails the job. | none (fake model) |
| Native .NET evaluation of recorded conversations | `cross-backend` | every push and PR | **Required** (same rule) | none (recorded) |
| Native Python evaluation | `repository-checks` → `evaluations` | every push and PR | **Required** | none (fake or recorded) |
| Fake-judge tests of the quality lane, and the report adapter tests | `dotnet` (solution tests) | every push and PR | **Required** | none |
| Model-judged quality (L32) | `Quality (live, billed)` workflow | **only when started by hand** (`workflow_dispatch`); needs the `ANTHROPIC_API_KEY` secret | never | at most 60 per run (worst case 53) |
| Report | `cross-backend`, and the quality workflow | after the evaluations, even if they failed | no: it shows results, it doesn't judge them | none |

Artifacts: `evaluation-results` (deterministic and native run documents, `comparison.md`, `report/report.html`, `report/report.json`, `report/store/`) and `quality-results` (the quality document and its report). Both are kept for 14 days. They contain synthetic fixture data only, and no credentials.

## How it works

1. **Run documents.** The evaluations write `evaluation-run` documents (one per backend, dataset and mode) and, for the live lane, an `evaluation-quality` document.
2. **`report`** (`Support.Evaluations/Reporting/`) checks each document against its schema, maps it to Microsoft's `ScenarioRunResult`, and writes them to a `DiskBasedResultStore` under one **execution name**. In CI that is `ci-<run id>-<attempt>` or `quality-<run id>-<attempt>`. The command refuses a document that breaks its schema, and two results that would land on the same scenario, so nothing is silently replaced.
3. **`dotnet aieval report`** (Microsoft.Extensions.AI.Evaluation.Console 10.11.0, pinned in `.config/dotnet-tools.json`) renders the store as HTML and as JSON.

### What the report keeps

| Concern | How it appears |
| --- | --- |
| **Targets** | Scenario `{scorer}.{backend}.{model mode}.{dataset}.{case}`, for example `contract-facts.python.fake.support-denial.refund-twice`; quality samples are `quality.{backend}.{case}` and `quality.calibration.{id}`. The report groups by the dots. |
| **Verdicts** | Each check is a boolean metric, *failed* when the check failed, with "expected X, got Y". Quality metrics are the judge's 1–5 numbers with its reason. |
| **Errors** | An errored case or judge failure is a metric with an **error diagnostic and no value**, marked failed. It is never a pass. A skip is *inconclusive*, with its reason. |
| **Run identity** | Tags: `run:<run id>`, `source-commit:<sha>`, `backend:`, `dataset:<id>@<version>`, `scorer:<name>@<version>`, `runner:<name>@<version>`, `judge:`, `package:<name>@<version>`. |
| **Provenance and cache** | Tag and metadata `provenance`: *fake model (no billable calls)*, *recorded responses (replayed, no model call)*, or *live*. Deterministic and native results have **no chat turns**, because the evaluation called no model. Each judge call in a quality result is a chat turn with its real model, latency and tokens, and `cacheHit: false`. The lane uses no response cache, and the tag `cache:not-used` says so. |
| **Unknown usage** | `usage:unknown` where the support contract doesn't report tokens (every backend turn). Unknown is never shown as zero. |
| **Estimates** | `estimate:backend-calls-counted-at-cap`: the quality budget counts each backend turn at its per-turn cap, because the real number isn't reported. |
| **Evidence** | Native results carry `evidence` metadata, the recording that was scored. |

Why no response cache: a cached judge answer would hide judge variability, and the budget counts real calls. The second live run below shows the variability a cache would have masked.

## Reproduce

```bash
# deterministic and native results (as CI)
dotnet build src/dotnet/Support.Api -c Release && dotnet build evaluations/dotnet/Support.Evaluations -c Release
dotnet run --project evaluations/dotnet/Support.Evaluations -c Release --no-build -- run --backend dotnet --launch --split development,heldout --source-commit "$(git rev-parse HEAD)"
dotnet run --project evaluations/dotnet/Support.Evaluations -c Release --no-build -- run --backend python --launch --split development,heldout --source-commit "$(git rev-parse HEAD)"
dotnet run --project evaluations/dotnet/Support.Evaluations -c Release --no-build -- native evaluate

# the report (no model calls)
export DOTNET_AIEVAL_TELEMETRY_OPTOUT=1   # the aieval tool collects anonymous usage data unless opted out
dotnet tool restore
dotnet run --project evaluations/dotnet/Support.Evaluations -c Release --no-build -- report --execution local
dotnet tool run aieval report --path evaluations/results/report/store --output evaluations/results/report/report.html
dotnet tool run aieval report --path evaluations/results/report/store --output evaluations/results/report/report.json --format json
```

The live lane is `quality --live` (see [quality.md](quality.md)), locally or through the *Quality (live, billed)* workflow.

## Curated summary (2026-11-12, assistant-run, local)

The report was built from 23 documents into **107 scenario results** (execution `local-l33`, source commit `a04648e` plus the L33 working tree):

| Results | Scenarios | Outcome |
| --- | --- | --- |
| Deterministic behaviour, .NET (fake) | 31 | all passed |
| Deterministic behaviour, Python (fake) | 31 | all passed |
| Native .NET, recorded | 11 | all passed |
| Native Python, fake and recorded | 22 | all passed |
| Quality, live (second run) | 12 | 12 scored; calibration 3/3 passed |

The second live quality run (schema 1.1) gave .NET relevance 3.75 and groundedness 4.25, and Python 4.0 and **4.5**. Python groundedness was 4.25 in the first run: the same configuration, one point of noise on one sample. Judge usage was 38,993 input and 6,588 output tokens over 21 calls. Each call is in the report as a fresh turn, about 1.5–2.5 s and 1.5–2.4k tokens each.

A failing deterministic case does fail: a copy of `support-success` whose `order-status` expected the wrong route ended with `failed order-status: step 0 route_target: expected "billing", got "orders"` and **exit code 1**.
