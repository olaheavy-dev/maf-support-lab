# Post-build evaluation and documentation plan

The application build phase completes at L21: the .NET and Python APIs build, pass their feature/contract checks, and agree in the cross-backend probes. (A frontend was planned; the owner dropped it on 2026-11-11, and the project closes once the backends are solid.) The formal evaluation and documentation phases follow that prerequisite. Implementation tickets still include tests, decision records, and learner evidence as they are built.

Stable ticket IDs and GitHub issue numbers are preserved. Use the roadmap's execution Order column, project Order field, and native blocked-by links. L16 moves into the evaluation phase; its ID is not an execution position.

## E08: evaluation, in execution order

| Ticket | Work | Main learning |
| --- | --- | --- |
| L29 | Dedicated evaluation projects, datasets, normalized results, and reset/budget rules | Separate business fixtures, evaluation data, scorers, and artifacts |
| L16 | Shared deterministic behavior runner against both completed backends | Assert routes, tools, authorization, state changes, and duplicate protection |
| L30 | Native .NET local evaluation and Microsoft evaluation abstractions | MAF orchestration, evaluator inputs, recorded responses, and local checks |
| L31 | Native Python local agent/workflow evaluation | Python evaluation protocols, tool context, and SDK differences |
| L32 | Budgeted model-based quality scoring | Consistent rubrics/context, judge variability, calibration, and cost |
| L33 | Microsoft reporting/CLI integration and separate CI lanes | Reporting adapters, caching provenance, reproducibility, and gate selection |

Evaluation dependencies stay under evaluations/dotnet/Support.Evaluations and evaluations/python, outside runtime API manifests. Dataset/result schemas are versioned. Raw run outputs and generated HTML reports are CI artifacts or ignored local outputs; selected sanitized summaries are Markdown under docs/evaluations.

Microsoft package versions are selected and verified at implementation time. Core, Quality, Reporting, and the local dotnet aieval tool have separate responsibilities. Safety and Azure report storage remain explicit optional extensions requiring their service prerequisites.

Local scoring can avoid judge calls while agent response generation can still call a model. Required CI uses fake or recorded responses. Live evaluation is explicitly invoked with bounded usage and recorded provenance. A high answer-quality score never substitutes for authorization or idempotency checks.

## E09: documentation

| Ticket | Work | Main learning |
| --- | --- | --- |
| L34 | Author canonical plain Markdown, a documentation index (docs/README.md), and a CI link check | Information architecture, evidence, and links that don't rot |

GitHub renders the Markdown directly: there is no site generator, hosted site, MDX, or duplicated copy (a Gatsby site was planned and dropped on 2026-11-11). The link checker is dependency-free and part of the required CI aggregate; it checks relative links, anchors, and images under docs/ and in the READMEs. Documentation checks never run agents, judges, or evaluations. L27 refreshes the Markdown and its index after the final operational/security review.

## Closing stages

After E08/E09, L25 expands the existing runner for fault/load exercises, L26 reviews operational/security evidence, L27 prepares the portfolio and refreshed Markdown documentation, and L28 completes interview practice with an independent read-only change.

Every added ticket carries pinned course references with their role explained, official documentation, implementation steps, prerequisites, scope boundaries, acceptance criteria, learning outcomes, interview questions, and an optional independent exercise.
