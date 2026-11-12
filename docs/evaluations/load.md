# Fault and bounded load (L25)

What happens to each backend under concurrency, and whether its guarantees still hold: no conversation sees another's data, no write runs twice, one failure stays in its own conversation. Fake model mode throughout, so this measures **our code** (HTTP, routing, MAF orchestration, storage), not a model provider. There is no cost to report: no provider call is made.

## The scenarios

`load --backend dotnet|python` in the evaluation runner (`Support.Evaluations/Load/`) launches the backend itself, one process per customer, and runs:

| Scenario | Workload | Must hold |
| --- | --- | --- |
| `isolation-c1`, `-c4`, `-c16` | 96 turns per level, split across 1, 4 or 16 concurrent conversations (both customers), each asking only about its own orders | every turn answers about the order it asked and names no other order; every turn belongs to the conversation that sent it; turn ids unique |
| `duplicate-submission` | one turn sent 16 times at once with the same `client_turn_id` | all accepted, all name **one** turn |
| `turn-in-progress` | 8 turns sent while that conversation's slow turn runs, then that turn is cancelled | all 8 get `409 turn_in_progress`; the cancel is accepted; the slow turn ends `cancelled` |
| `duplicate-approval` | one cancellation approved 16 times at once (fresh store) | all 200 and the same resumed turn; the operation `committed`; the order shows the change once |
| `decision-race` | 8 approvals and 8 rejections of one refund at once (fresh store) | exactly one decision wins; every request for the other decision gets `409 approval_already_decided`; the operation matches the winner |
| `faults-under-load` | 16 conversations at once, half with an injected model failure | the failures are `failed / internal_error` (as the `support-failure` dataset expects); the healthy half completes; a failed conversation's next turn completes |

The checks are correctness only, with no timing thresholds, so they are deterministic. CI runs them on both backends in `cross-backend` (required). Latency and throughput are recorded in `load-<backend>.json` (schema `evaluation-load` 1.0, in the `evaluation-results` artifact) and are **never gated**, because CI hardware varies.

## Results (assistant-run, 2026-11-12)

**Target:** macOS, 18 CPUs, Release build (.NET) and uv-locked (Python), loopback HTTP, with the runner on the same machine. Each row is the range over **3 runs**, with one warm-up pass before measuring. Every check passed in every run, on both backends.

| Scenario | .NET req/s | .NET p50 / p95 ms | Python req/s | Python p50 / p95 ms |
| --- | --- | --- | --- | --- |
| isolation, 1 conversation | 750–866 | 1.0–1.2 / 1.5–2.0 | 117–137 | 7.2–8.4 / 8.1–10.0 |
| isolation, 4 at once | 1,174–1,366 | 1.0–1.7 / 2.6–6.5 | 244–270 | 14.8–16.3 / 18.0–20.5 |
| isolation, 16 at once | 1,185–2,355 | 1.7–2.4 / 21.8–37.0 | 278–302 | 55.7–61.2 / 66.6–77.7 |
| faults under load (16) | 363–673 | 1.5–3.0 / 21.6–27.6 | 112–120 | 105–124 / 147–175 |
| duplicate approval (16) | 431–611 | 25–36 / 26–36 | 464–500 | 21–23 / 31–34 |
| decision race (16) | 454–868 | 9–12 / 18–35 | 505–613 | 19–23 / 26–32 |

Request counts per scenario are in the result documents: for example, 97–112 for isolation (96 turns plus conversation creation) and 16 for the races.

## Bottlenecks

- **.NET, before the fix: storage connections.** `SupportDatabase` opened a fresh SQLite connection (open plus pragmas) for every call. Under concurrent writes this showed a cluster of **150–190 ms** tail latencies (over 3 runs: duplicate-approval p95 186 and 156 ms in two runs and 37.5 ms in the third; decision-race p95 156–179 ms in all three), against medians of 10–36 ms.
- **Python: one event loop.** Throughput plateaus near 300 req/s, and the median grows with concurrency (7 → 15 → 58 ms): requests queue on a single Uvicorn worker. Storage also opens a connection per call, synchronously on the loop.
- **Python: the failure path.** With half the turns failing, throughput drops to about 115 req/s and the median to about 110 ms, roughly 2.5× slower than all-success load. A failed turn costs much more than a successful one there. **Not investigated further.** Candidates are exception classification and logging and telemetry on the failure path; the next step would be a profile of one failed turn.

## The improvement: pooled SQLite connections (.NET)

One setting changed: `Pooling = false` → `Pooling = true` in `SupportDatabase`. The business contract and the transaction rules were not touched (writes still use one `BEGIN IMMEDIATE` transaction with guarded status changes; [0003](../decisions/0003-state-and-idempotency.md)).

| .NET, 3 runs each, concurrency 16 | Before | After |
| --- | --- | --- |
| duplicate-approval p95 | 37.5–186 ms (2 of 3 runs over 150 ms) | 25.7–36.4 ms |
| decision-race p95 | 156–179 ms | 18.3–35.0 ms |
| isolation-c16 throughput | 608–686 req/s | 1,185–2,355 req/s |
| isolation-c16 p50 | 4.5–5.4 ms | 1.7–2.4 ms |

**The shared suite still passes:**
- the full .NET suite, three times;
- every evaluation dataset on both backends in launch mode, including the restart and resume cases;
- the 29 cross-backend probes;
- every load check.

The "before" isolation numbers come from an earlier measurement pass at concurrency 16 only; the "after" numbers are from the final runs.

## Limitations

- **Fake model.** A real model adds seconds per turn and dominates latency. These numbers bound our own overhead, not end-to-end response time.
- **One machine.** The load generator and the backend share CPUs. Absolute numbers are machine-specific; compare them only within one table.
- **Small, bounded samples.** Each table row comes from 3 runs of 16–112 requests per scenario. The ranges show run-to-run spread; they aren't confidence intervals.
- **No live load.** A live run would mostly measure the provider and cost money per turn. The per-turn model-call cap already bounds one turn's cost ([observability](../observability.md#failures-retries-and-budgets-l15)).
- **One process per database file.** Neither backend supports several processes on one store, so horizontal scaling isn't measured.

Reproduce:

```bash
dotnet build src/dotnet/Support.Api -c Release && dotnet build evaluations/dotnet/Support.Evaluations -c Release
dotnet run --project evaluations/dotnet/Support.Evaluations -c Release --no-build -- load --backend dotnet --source-commit "$(git rev-parse HEAD)"
dotnet run --project evaluations/dotnet/Support.Evaluations -c Release --no-build -- load --backend python --source-commit "$(git rev-parse HEAD)"
```
