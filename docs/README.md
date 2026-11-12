# Documentation

Plain Markdown, read on GitHub: no site and no build. Every page under `docs/` is reachable from here, and CI checks every relative link, anchor and image (`scripts/check_docs_links.py`).

## Reading order

1. [Architecture](architecture.md): the pieces, one turn end to end, state, and the two SDKs.
2. [Demonstration](demo.md): the system's behaviour on both backends, with real output from a fresh clone.
3. [Runbook](runbook.md): prerequisites, running both backends, exercising the API, the CI checks, common problems.
4. [API, approval, and state flow](api-flow.md): turns, approvals, operations, streaming and cancellation.
5. [Threat model](threat-model.md): identity, the access matrix, prompt injection, operation risk, and known gaps.
6. [Observability](observability.md): traces, metrics and logs in Aspire; failures, retries and budgets.
7. [Testing](testing.md): offline, reproducible and live checks.
8. [Evaluations](evaluations/README.md): behaviour baseline, model-judged quality, reports and CI lanes.

## Reference

| Page | What it holds |
| --- | --- |
| [Contract](../contracts/README.md) | Version 1 of the API: routes, turns, approvals, streaming, errors (normative) |
| [.NET backend](../src/dotnet/README.md) | Code layout, configuration, and each feature in `Support.Api` |
| [Python backend](../src/python/README.md) | Modules, configuration, and each feature in `support_agent` |
| [Fixtures](../fixtures/README.md) | Synthetic data, eligibility rules, scenarios |
| [Evaluation tooling](../evaluations/README.md) | Datasets, schemas, the runner and its commands |
| [Version policy](version-policy.md) | How versions are chosen |
| [Repository settings](repository-settings.md) | Branch protection and required checks |

### Decisions

| Decision | Status |
| --- | --- |
| [0001 - Version baseline and model provider](decisions/0001-version-baseline.md) | current: .NET 11, MAF 1.28.0 / 1.25.0, Aspire 13.6.5, `claude-haiku-5-5` |
| [0002 - Code layout](decisions/0002-code-layout.md) | current |
| [0003 - Durable state and idempotent writes](decisions/0003-state-and-idempotency.md) | current |
| [0004 - SDK comparison](decisions/0004-sdk-comparison.md) | current |
| [0006 - Browser integration](decisions/0006-browser-integration.md) | superseded (the frontend was retired on 2026-11-11) |

0005 was reserved for a retired ticket and was never written.

### Evaluation results

| Page | What it holds |
| --- | --- |
| [Evaluation results](evaluations/README.md) | Curated summaries, and how to regenerate them |
| [Baseline](evaluations/baseline.md) | Deterministic behaviour on both backends, side by side |
| [Quality](evaluations/quality.md) | The model-judged lane: method, live results, limitations |
| [Fault and load](evaluations/load.md) | Concurrency and fault scenarios, latency and throughput on both backends, bottlenecks |
| [Reports and CI lanes](evaluations/reports.md) | The Microsoft evaluation report, what gates a merge, the curated summary |

## Credits

The use case and its starting point come from Dometrain's Microsoft Agent Framework courses (repositories `getting-started-microsoft-agent-framework-in-dotnet` and `…-in-python`): the [.NET capstone](https://github.com/Dometrain/getting-started-microsoft-agent-framework-in-dotnet/tree/014ca9f4ee3764d18b259246310941a07b13541b/src/Chapter07.VoltCartSupport) and the [Python capstone](https://github.com/Dometrain/getting-started-microsoft-agent-framework-in-python/tree/ebd05e88679a93113fe2a3223092be125752e4ba/chapter_07). Links point to pinned commits; no course code or transcripts are copied. The implementation follows the released Microsoft Agent Framework, Microsoft.Extensions.AI and Aspire packages and their documentation. The official documentation and release notes relied on are listed in [sources.json](sources.json) with the date each was checked.
