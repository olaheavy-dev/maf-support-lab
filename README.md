# MAF Support Lab

A learning project: build the same customer-support system with Microsoft Agent Framework in C# and Python, orchestrate local development with Aspire, and verify both backends against one shared contract.

## Current state

Both backends are built and verified: .NET (ASP.NET Core, MAF 1.28.0, .NET 11) and Python (FastAPI, MAF 1.25.0, Python 3.15). Each has triage and routing, read-only specialists, bound approvals for simulated writes, durable SQLite state, streaming and cancellation, access and prompt-injection tests, bounded retries and budgets, and OpenTelemetry into Aspire. Both agree on 29 cross-backend contract probes and every evaluation dataset. A budgeted, model-judged quality lane and Microsoft evaluation reports sit on top. The remaining tickets are fault and load exercises, a security review, the final runbook and portfolio, and a mock interview.

## Start here

- **Understand the system:** [docs/README.md](docs/README.md), the documentation index. It starts with the [architecture](docs/architecture.md).
- **Run it:** the [runbook](docs/runbook.md). `dotnet run --project apphost` starts both backends in fake mode; no key is needed.
- **Follow the learning path:** [the workflow](docs/learning/workflow.md), then [the roadmap](docs/learning/roadmap.md#ticket-execution-order). Work one ticket and one pull request at a time.

## Layout

    apphost/                      C# Aspire AppHost: both backends and the Python DevUI
    src/dotnet/                   ASP.NET Core / MAF implementation (Support.Api) and its tests
    src/python/                   Python / MAF implementation with uv (support_agent) and its tests
    contracts/                    Version 1 schemas, examples, and the cross-backend probes
    fixtures/                     Synthetic customers, orders, invoices, payments, knowledge base, policies, scenarios
    evaluations/                  Datasets, the evaluation runner (behaviour, native MAF, model-judged), reports
    docs/                         Architecture, runbook, decisions, evaluations, learning evidence
    scripts/                      Roadmap rendering and validation, the documentation link check

The two backends implement the same use case independently. They share the contract and the synthetic fixtures; conversations and mutable state stay isolated per backend, and each configures its own OpenTelemetry.

## References and versions

The course capstone supplies the use case. Implementation follows compatible released packages, migration notes, official documentation, and release-tagged samples.
See [sources](docs/sources.json), [the initial audit](docs/course-audit.md), and [version policy](docs/version-policy.md).

## Working agreements

- One short-lived branch per ticket, for example feat/l05-order-status.
- Pull requests target main; squash merge after the required checks pass.
- No model credentials, customer data, or private course transcripts in commits or issues.
- Offline CI uses fakes. Live-model evaluations are explicit, bounded, and separately reported.
- Preserve source attribution; link to course material rather than copying entire course projects.

See [contribution guidance](CONTRIBUTING.md) and [repository settings](docs/repository-settings.md).

Ticket scope and parity requirements are defined in the [alignment contract](docs/learning/alignment.md). The [ticket review](docs/learning/ticket-review.md) records the corrections and verification limits; the [capability map](docs/learning/capabilities.md) identifies which earlier tasks supply each prerequisite.

## GitHub learning board

[Project board](https://github.com/orgs/olaheavy-dev/projects/4) · [First ticket: L01](https://github.com/olaheavy-dev/maf-support-lab/issues/8) · [All issues](https://github.com/olaheavy-dev/maf-support-lab/issues)

The board contains 39 active learning tickets and eight parent epics linked through native sub-issues; five retired tickets and one retired epic stay closed for history (see the roadmap). Follow the execution Order field and blocking links; stable ticket IDs never change or get reused. The first unblocked ticket is Ready; later tickets are blocked by their execution predecessor.

Build and verify the .NET and Python APIs first (through L21). E08 then introduces six formal evaluation tasks, and E09 one documentation task: canonical plain Markdown that GitHub renders, with a documentation index and a CI link check. The project closes once the backends are solid; a frontend and a generated documentation site were dropped on 2026-11-11 (see the roadmap's retired tickets).
