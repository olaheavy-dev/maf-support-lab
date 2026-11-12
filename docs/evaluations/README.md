# Evaluation results

Curated, sanitized evaluation summaries. Raw run documents are never committed: CI uploads them as the `evaluation-results` artifact, and locally they go to `evaluations/results/`, which Git ignores. How the evaluations work is described in [evaluations/README.md](../../evaluations/README.md).

| Summary | What it shows |
| --- | --- |
| Native .NET evaluation (L30) | `native evaluate` scores `evaluations/recordings` with MAF's local evaluator in CI (`cross-backend`); its run documents are in the `evaluation-results` artifact. |
| [quality.md](quality.md) | The model-judged quality lane (L32): method, live results with calibration, and limitations. |
| [reports.md](reports.md) | The Microsoft evaluation report and CI lanes (L33): what gates a merge, what the report keeps, how to reproduce it, and the curated summary. |
| [load.md](load.md) | Fault and bounded load (L25): isolation, duplicate submissions and approvals, decision races and contained failures under concurrency on both backends; latency and throughput; the bottlenecks; the pooled-connection improvement. |
| [baseline.md](baseline.md) | The deterministic behaviour baseline (L16): every dataset on both backends in fake mode, with each run's identity, each case side by side, agreement, and latency. |

## Regenerating the baseline

```bash
dotnet build src/dotnet/Support.Api -c Release
dotnet build evaluations/dotnet/Support.Evaluations -c Release
for b in dotnet python; do
  dotnet run --project evaluations/dotnet/Support.Evaluations -c Release --no-build -- \
    run --backend $b --launch --split development,heldout --source-commit $(git rev-parse HEAD)
done
dotnet run --project evaluations/dotnet/Support.Evaluations -c Release --no-build -- \
  compare --results evaluations/results --out docs/evaluations/baseline.md
```

Statuses and agreement are deterministic. Latency and run ids change on every run; that's expected.
