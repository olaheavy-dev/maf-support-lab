# Model-judged quality

The quality lane asks both backends the same questions live, then has an AI judge score each reply for **relevance** and **groundedness** with Microsoft's `Microsoft.Extensions.AI.Evaluation.Quality` evaluators. It is a measurement of reply quality, opt-in and budgeted. It is not a gate.

> **What it cannot tell you.** A judge reads text. It cannot see whether a write was authorized, whether an approval happened, or whether something ran exactly once. Those facts come from the deterministic lanes (the behaviour runner over the API, native checks, access and injection tests), which stay authoritative. A reply can score 5 for groundedness and still describe something that never happened in the process. The run below has an example.

## How it works

| Piece | What it does |
| --- | --- |
| `evaluations/config/quality.json` | Pins everything that affects a score: the judge (`claude-haiku-5-5`, max output 1024), the backends' live model (`claude-haiku-5-5`) and per-turn cap (4 model calls), the metrics, the 4 sampled cases, the budget (60 calls, 1 repetition, concurrency 2), and the calibration answers. |
| `quality --live` (`Support.Evaluations/Quality/`) | Starts each backend in **live** mode (one process per customer), sends each case's question in a new conversation, and judges the reply. Without `--live` it refuses to run, and CI never passes it. |
| Grounding context | The case's `context` references (`orders:ORD-1002`, `policies:refund`, …) resolved to the exact fixture records, plus the fixture clock when a policy is referenced. It is the evidence a correct reply could use, not a gold answer. A missing or unknown reference is an error, never a score. |
| Rubric | The evaluators' built-in prompts, unchanged: 1–5, higher is better. |
| Budget | Every model call, backend or judge, comes out of one budget. A backend turn reserves its per-turn cap up front, because the contract doesn't report how many calls a turn made. Each judge metric reserves 1. Samples are admitted in plan order, and whatever doesn't fit is recorded as `skipped: budget`. The judge client refuses any call that no reservation covers. The worst case for the committed configuration is 4 × 2 × 4 + 8 × 2 + 5 = **53 of 60**. |
| Statuses | A metric is `scored` only with a value and no error diagnostic. A judge exception, malformed judge output, missing context, or a backend turn that didn't complete is `error`. Budget or cancellation is `skipped`. None of these turns into a score. |
| Calibration | Fixed answers with known quality: a good answer, a contradicting one, an off-topic one, and (the stretch) the good answer judged against a different order's record. For each metric, every "high" answer must score above every "low" one. |
| Result | `evaluations/results/quality-live.json`, schema `evaluations/schemas/evaluation-quality.schema.json` (1.1: each metric keeps the evaluator's per-call metadata, and the run states that no response cache is used; 1.0 documents are still read). It records the judge, models, package versions, metrics, rubric, context, answers, repetitions, budget, judge token usage, and an uncertainty statement. It is ignored by Git. |

Run it (it is billed; `ANTHROPIC_API_KEY` must be set, and is passed to the backends and the judge, never printed):

```bash
dotnet build src/dotnet/Support.Api -c Release
dotnet build evaluations/dotnet/Support.Evaluations -c Release
dotnet run --project evaluations/dotnet/Support.Evaluations -c Release --no-build -- quality --live --source-commit "$(git rev-parse HEAD)"
```

In CI the lane is the *Quality (live, billed)* workflow. It runs only when started by hand, needs the `ANTHROPIC_API_KEY` repository secret, and uploads the result and its report ([reports.md](reports.md)).

The fake-judge tests (`Support.Evaluations.Tests/QualityTests.cs`) run in CI and need no model. They cover the budget, judge errors, malformed output, missing context, failed turns, concurrency, cancellation, unreserved calls, calibration failure, and the command's refusals.

## Results: live run on 2026-11-12 (assistant-run)

Source commit `3e61ec3` plus the working tree of this change. Judge and backends on `claude-haiku-5-5`. Packages: Quality 10.11.0, Evaluation 10.11.0, Anthropic 13.7.0. 1 repetition, concurrency 2. **12 of 12 samples scored**, 0 errors, 0 skipped. 53 of 60 calls reserved, 21 of them judge calls. Judge usage: 39,021 input and 6,555 output tokens. The run took 44 s.

| Case | .NET relevance | .NET groundedness | Python relevance | Python groundedness |
| --- | --- | --- | --- | --- |
| order-status | 3 | 4 | 4 | 5 |
| invoice | 4 | 5 | 4 | 4 |
| knowledge-base | 4 | 4 | 4 | 4 |
| refund-outside-window | 4 | 4 | 4 | 4 |
| **Mean** | **3.75** | **4.25** | **4.0** | **4.25** |

| Calibration | High | Low | Result |
| --- | --- | --- | --- |
| relevance: good vs off-topic | 4 | 1 | passed |
| groundedness: good vs contradicting | 5 | 3 | passed |
| groundedness: good vs changed context (stretch) | 5 | 3 | passed |

**How to read it:**
- **Don't rank the backends from this.** The two are within one point everywhere. With one sample each and a nondeterministic model on both sides, a one-point difference is noise. The .NET order-status relevance of 3 came from its reply giving dates and items but no "not delivered yet". The judge called that incomplete for "where is my order".
- **The judge compresses the scale.** It separated good from bad every time, but the changed-context answer got 3 even though its own reason called the details "fabricated… ungrounded and incorrect". The contradicting answer also got 3. Treat scores as relative (above or below calibration), not as absolute thresholds.
- **Groundedness can't check process claims.** The .NET refund-denial reply said "the approver declined the request". The fixture context can neither support nor refute that, so the judge still gave it 4. Whether an approval happened is answered by the operation record in the behaviour lane, not by a judge.
- **The evaluators were tuned for GPT-4o.** Their documentation says so, and that results vary with other models. Haiku was chosen for cost (the owner's decision); the calibration is the check that it still discriminates.

## Limitations

- **Small sample.** 4 cases, 1 repetition, 1 judge. More repetitions would give a spread, at a proportional cost.
- **Narrow context.** Context is what the dataset lists. A reply that correctly uses a record not listed is marked down for groundedness. This is why the payment-history case, whose context lists one payment of three, isn't sampled.
- **Backend calls are counted at the cap.** The contract doesn't report model calls per turn, so the budget is conservative and the actual backend spend is unknown.
- **Wording varies.** Live replies change between runs, so scores will too. Only the committed calibration answers are fixed.

## Optional follow-up: safety evaluators

`Microsoft.Extensions.AI.Evaluation.Safety` (content harm, protected material, indirect attack, code vulnerability, and `GroundednessProEvaluator`, a fine-tuned groundedness model) runs its evaluations in the **Microsoft Foundry Evaluation service** ([package overview](https://learn.microsoft.com/en-us/dotnet/ai/evaluation/libraries), checked 2026-11-12), so it needs a Foundry project and credentials. The default lane deliberately needs no new cloud subscription. Adding safety evaluators would be a separate, opt-in ticket: configure a Foundry project, add the Safety package to the evaluation project only, and route its results through the same budget and status rules.
