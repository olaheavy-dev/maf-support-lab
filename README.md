# MAF Support Lab

A learning project: build the same customer-support system with Microsoft Agent Framework in C# and Python, orchestrate local development with Aspire, and add one frontend after the backend contracts work.

## Current state

Repository governance and the learning roadmap are prepared. Application implementation starts with L01. No running support application is claimed yet.

## Start here

1. Read [the learning workflow](docs/learning/workflow.md).
2. Open [the ordered roadmap](docs/learning/roadmap.md) and pick the first uncompleted, unblocked ticket.
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
    frontend/                     Added after backend parity
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

## GitHub learning board

[Project board](https://github.com/users/sunkanmi-olawuwo/projects/2) · [First ticket: L01](https://github.com/sunkanmi-olawuwo/maf-support-lab/issues/8) · [All issues](https://github.com/sunkanmi-olawuwo/maf-support-lab/issues)

The board contains 28 learning tickets; seven parent epics are linked through native sub-issues. L01 is Ready; later tickets are blocked by their predecessor.
