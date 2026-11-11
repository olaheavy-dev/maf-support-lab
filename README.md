# MAF Support Lab

A learning project: build the same customer-support system with Microsoft Agent Framework in C# and Python, orchestrate local development with Aspire, and verify both backends against one shared contract.

## Current state

Repository governance and the learning roadmap are prepared. Application implementation starts with L01. No running support application is claimed yet.

## Start here

1. Read [the learning workflow](docs/learning/workflow.md).
2. Open [the roadmap diagram](docs/learning/roadmap.md#learning-path-at-a-glance) and [ticket execution order](docs/learning/roadmap.md#ticket-execution-order), then pick the first uncompleted, unblocked ticket.
3. Read its pinned course files and official documentation before implementing.
4. Work on one ticket and one pull request at a time.
5. Demonstrate the result and explain the interview questions before moving on.

## Planned layout

    apphost/                      C# Aspire AppHost
    src/dotnet/                   ASP.NET Core / MAF implementation
    src/python/                   Python / MAF implementation with uv
    contracts/                    Shared request, streaming, approval, and error schemas
    fixtures/                     Synthetic customer/order/policy data
    evaluations/                  Shared behavioral scenarios
    docs/                         Decisions, sources, learning evidence

The two backends implement the same use case independently. They share contracts and synthetic fixtures; conversations and mutable state remain isolated per backend. Each service will explicitly configure its own OpenTelemetry instrumentation and export.

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

The board contains 38 active learning tickets and eight parent epics linked through native sub-issues; five retired tickets and one retired epic stay closed for history (see the roadmap). Follow the execution Order field and blocking links; stable ticket IDs never change or get reused. The first unblocked ticket is Ready; later tickets are blocked by their execution predecessor.

Build and verify the .NET and Python APIs first (through L21). E08 then introduces six formal evaluation tasks, and E09 one documentation task: canonical plain Markdown that GitHub renders, with a documentation index and a CI link check. The project closes once the backends are solid; a frontend and a generated documentation site were dropped on 2026-11-11 (see the roadmap's retired tickets).
