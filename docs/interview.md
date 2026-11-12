# Interview explanations

> **Assistant-drafted.** These are study notes the assistant wrote from the finished code and evidence, to explain the system in an interview. They are not the owner's own answers; the owner practises those (L28). Each topic links to the evidence behind it.

## The 60-second version

Two support backends, C# and Python, built natively on Microsoft Agent Framework. They are verified against one contract, so either could serve the same client.
- **Routing:** a triage agent classifies each message, and code routes it to an orders, billing or technical specialist that can only use its own tools.
- **Writes:** writes are proposals. The customer approves an operation bound to the exact arguments, and it runs at most once, surviving restarts.
- **Identity:** it is server-owned, and everything owned by someone else looks missing.
- **Verification:** the two are compared on every push with black-box probes and evaluation datasets. A budgeted, model-judged lane measures reply quality but never gates. Everything is traced in Aspire.

## Topics

| Question | Short explanation | Evidence |
| --- | --- | --- |
| Why can't the model approve or authorize anything? | Identity comes from the host, ownership is checked in application services, and writes are approval-required tools whose arguments are bound before the customer decides. The model can only *propose*. | [threat model](threat-model.md#core-rules), [api flow](api-flow.md#a-write-propose-decide-execute-once) |
| How do you make a write exactly-once across retries and crashes? | Decide once with a guarded status change, then write and mark committed in one transaction. A repeated decision returns the stored result. The paused request is persisted, so a restart can resume it. | [0003](decisions/0003-state-and-idempotency.md) |
| Why is triage output treated as data? | It is parsed against the full contract schema, and rules in code check the action matches the target. Invalid output yields a clarification, never a guessed route. | [architecture](architecture.md#one-turn) |
| How do you defend against prompt injection? | Make it harmless rather than detect it: tools take ids only, never a customer id; ownership is enforced in code; writes need approval. Injection signals are diagnostic only. Tested with an "obedient attacker" fake model. | [threat model: prompt injection](threat-model.md#prompt-injection-l14) |
| How do you keep two SDKs consistent? | One versioned contract, 29 cross-backend HTTP probes that must agree, and the same evaluation datasets on both. SDK differences are documented, and none is visible to a client. | [0004](decisions/0004-sdk-comparison.md) |
| How do you bound cost and latency? | The SDK owns retries; each turn has a deadline and a model-call budget; tools have time limits; failures are classified into contract error codes. | [observability: budgets](observability.md#failures-retries-and-budgets-l15) |
| What can an LLM judge tell you, and what can't it? | Relevance and groundedness of a reply, relative to calibration answers. It can't establish authorization or exactly-once execution. The live run had a reply claiming "the approver declined", which scored 4 for groundedness anyway. | [quality](evaluations/quality.md#results-live-run-on-2026-11-12-assistant-run) |
| How do you compare two backends fairly with a noisy judge? | Same questions, context, rubric and judge, all pinned and recorded; calibration first; repetitions for a spread. A one-point gap on one sample is noise: the same configuration moved 0.25 between runs. | [quality](evaluations/quality.md#limitations), [reports](evaluations/reports.md#curated-summary-2026-11-12-assistant-run-local) |
| What gates a merge? | Deterministic behaviour, native evaluations, contracts, access and injection tests, and link checks: all offline and repeatable. Model-judged scores run on demand, with a budget, and are reported, not gated. | [reports: lanes](evaluations/reports.md#lanes) |
| How do you observe a turn? | One trace per turn: agent, model and tool spans, with token usage counted once. The contract's `trace_id` is the real trace id. Metrics and structured logs go to the Aspire dashboard. | [observability](observability.md#one-turn-as-a-trace) |

## Tradeoffs worth defending

- **One project with layered folders and architecture tests,** rather than a project per layer: the boundaries are enforced without the ceremony ([0002](decisions/0002-code-layout.md)).
- **SQLite per backend,** not a shared database: isolation and simple restarts, at the cost of one process per file ([0003](decisions/0003-state-and-idempotency.md#5-what-this-does-not-guarantee)).
- **.NET 11 (STS):** it has the same end of support as .NET 10 LTS, 2028-11-14 ([0001](decisions/0001-version-baseline.md)).
- **No response cache in the live judge lane:** a cache would hide judge variability ([reports](evaluations/reports.md)).
