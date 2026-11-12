# Fault and bounded load

What happens to each backend under concurrency, and whether its guarantees still hold: no conversation sees another's data, no write runs twice, one failure stays in its own conversation. Fake model mode throughout, so this measures **our code** (HTTP, routing, MAF orchestration, storage), not a model provider. There is no cost to report: no provider call is made.

## The scenarios

`load --backend dotnet|python` in the evaluation runner (`Support.Evaluations/Load/`) launches the backend itself, one process per customer, and runs:

| Scenario | Workload | Must hold |
| --- | --- | --- |
| `isolation-c1`, `-c4`, `-c16` | 96 turns per level, split across 1, 4 or 16 concurrent conversations (both customers), each asking only about its own orders | every turn answers about the order it asked and names no other order; every turn belongs to the conversation that sent it; turn ids unique |
| `duplicate-submission` | one turn sent 16 times at once with the same `client_turn_id` | all accepted, all name **one** turn |
| `turn-in-progress` | 8 turns sent while that conversation's slow turn runs, then that turn is cancelled | all 8 get `409 turn_in_progress`; the cancel is accepted; the slow turn ends `cancelled` |
| `duplicate-approval` | one support ticket approved 16 times at once (fresh store), then one probe ticket approved once | all 200 and the same resumed turn; the operation committed `TKT-0001`; **the probe is `TKT-0002`**, so the write ran exactly once |
| `decision-race` | 8 approvals and 8 rejections of one ticket at once (fresh store), then one probe ticket | exactly one decision wins; every request for the other decision gets `409 approval_already_decided`; the operation matches the winner; **the probe is `TKT-0002` if approve won, `TKT-0001` if reject won** |
| `faults-under-load` | 16 conversations at once, half with an injected model failure | the failures are `failed / internal_error` (as the `support-failure` dataset expects); the healthy half completes; a failed conversation's next turn completes |

**Why a probe ticket:** a final status of `committed`, or an order showing `cancelled`, can't tell one write from several. Tickets are numbered from a stored counter in each store, so the next ticket's number counts the writes (Codex review).

The checks are correctness only, with no timing thresholds, so they are deterministic. CI runs them on both backends in `cross-backend` (required). Latency and throughput are recorded in `load-<backend>.json` (schema `evaluation-load` 1.0, in the `evaluation-results` artifact) and are **never gated**, because CI hardware varies.

## Results (assistant-run, 2026-11-12)

**Target:** macOS, 18 CPUs, Release build (.NET) and uv-locked (Python), loopback HTTP, with the runner on the same machine. Each row is the range over **3 runs**, with one warm-up pass before measuring. Every check passed in every run, on both backends. Both columns include their backend's storage fix (below); the Python column was re-measured after its fix.

| Scenario | .NET req/s | .NET p50 / p95 ms | Python req/s | Python p50 / p95 ms |
| --- | --- | --- | --- | --- |
| isolation, 1 conversation | 680–779 | 1.2–1.3 / 1.4–2.2 | 238–243 | 4.1 / 4.7–5.0 |
| isolation, 4 at once | 1,217–1,420 | 1.3–1.9 / 5.7–6.6 | 496–512 | 8.0–8.2 / 9.0 |
| isolation, 16 at once | 1,533–1,673 | 2.2–2.5 / 21.8–22.9 | 607–646 | 26.5–28.2 / 31.6–34.2 |
| faults under load (16) | 319–506 | 2.4–3.2 / 31.6–69.3 | 222–224 | 53–57 / 91–97 |
| duplicate approval (16) | 598–644 | 24–25 / 25–26 | 1,393–1,459 | 8.7–9.1 / 10.3–10.7 |
| decision race (16) | 458–906 | 10–12 / 17–35 | 1,355–1,632 | 7.9–9.4 / 9.6–11.4 |

Request counts per scenario are in the result documents: for example, 97–112 for isolation (96 turns plus conversation creation) and 16 for the races.

## Bottlenecks

- **.NET, before the fix: storage connections.** `SupportDatabase` opened a fresh SQLite connection (open plus pragmas) for every call. Under concurrent writes this showed a cluster of **150–190 ms** tail latencies (over 3 runs: duplicate-approval p95 186 and 156 ms in two runs and 37.5 ms in the third; decision-race p95 156–179 ms in all three), against medians of 10–36 ms.
- **Python, before its fix: storage connections on the event loop.** Throughput plateaued near 300 req/s, and the median grew with concurrency (7 → 15 → 53 ms). A profile of the running server showed every database call opening a connection, setting WAL and closing it again, synchronously on the single event loop: 245 connects and closes for 80 turns, about 2.5 ms of a 10–12 ms turn.
- **Python, still: one event loop.** After the fix the median still grows with concurrency (4 → 8 → 27 ms), because one Uvicorn worker serves every request. More workers would need a store shared across processes (see the limitations).
- **Not a bottleneck: the failure path.** An earlier version of this page said failed turns were about 2.5× slower. That was a measurement artifact. The isolation scenario splits its conversations across two processes (one per customer), while the faults scenario puts all 16 on one process, so it carries twice the load per process. Measured one at a time on the same process, a failed turn takes 10.3 ms against 11.5 ms for a successful one.

## The improvement: pooled SQLite connections (.NET)

One setting changed: `Pooling = false` → `Pooling = true` in `SupportDatabase`. The business contract and the transaction rules were not touched (writes still use one `BEGIN IMMEDIATE` transaction with guarded status changes; [0003](../decisions/0003-state-and-idempotency.md)).

| .NET, 3 runs each, concurrency 16 | Before | After |
| --- | --- | --- |
| duplicate-approval p95 | 37.5–186 ms (2 of 3 runs over 150 ms) | 25–27 ms (pooling experiment) |
| decision-race p95 | 156–179 ms | 20–22 ms (pooling experiment) |
| isolation-c16 throughput | 608–686 req/s | 1,533–1,673 req/s |
| isolation-c16 p50 | 4.5–5.4 ms | 2.2–2.5 ms |

**The shared suite still passes:**
- the full .NET suite, three times;
- every evaluation dataset on both backends in launch mode, including the restart and resume cases;
- the 29 cross-backend probes;
- every load check.

The before/after write rows come from the pooling experiment (concurrency 16 only, 3 runs each), which ran when these scenarios still wrote a cancellation and a refund; the throughput and p50 "after" values are from the final runs.

## The improvement: reused SQLite connections (Python)

`Database` now reuses one autocommit connection per thread for plain reads and writes, instead of opening one per call. Transactions (`atomically()`) still get their own connection, so coroutines interleaving on the event loop never share a transaction. Each call is a single synchronous statement. At shutdown, `close()` closes the reused connections before the final WAL checkpoint, and the class returns itself when copied, because MAF deep-copies workflow state.

| Python, 3 runs each | Before | After |
| --- | --- | --- |
| isolation-c16 throughput | 274–322 req/s | 607–646 req/s |
| isolation-c16 p50 | 51.8–55.3 ms | 26.5–28.2 ms |
| isolation-c1 p50 | 7.0–7.8 ms | 4.1 ms |
| duplicate-approval throughput | 359–517 req/s | 1,393–1,459 req/s |
| decision-race throughput | 572–642 req/s | 1,355–1,632 req/s |

**Still passing:** all 267 Python tests (durable-state and restart tests included), every load check, every launch-mode evaluation dataset, and the 29 cross-backend probes.

## Limitations

- **Fake model.** A real model adds seconds per turn and dominates latency. These numbers bound our own overhead, not end-to-end response time.
- **One machine.** The load generator and the backend share CPUs. Absolute numbers are machine-specific; compare them only within one table.
- **Small, bounded samples.** Each table row comes from 3 runs of 16–112 requests per scenario. The ranges show run-to-run spread; they aren't confidence intervals.
- **No live load.** A live run would mostly measure the provider and cost money per turn. The per-turn model-call cap already bounds one turn's cost ([observability](../observability.md#failures-retries-and-budgets)).
- **One process per database file.** Neither backend supports several processes on one store, so horizontal scaling isn't measured.

Reproduce:

```bash
dotnet build src/dotnet/Support.Api -c Release && dotnet build evaluations/dotnet/Support.Evaluations -c Release
dotnet run --project evaluations/dotnet/Support.Evaluations -c Release --no-build -- load --backend dotnet --source-commit "$(git rev-parse HEAD)"
dotnet run --project evaluations/dotnet/Support.Evaluations -c Release --no-build -- load --backend python --source-commit "$(git rev-parse HEAD)"
```
