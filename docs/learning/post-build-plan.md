# Post-build evaluation and Gatsby documentation plan

The application build phase completes at L24: the .NET API, Python API, and shared frontend build and run with their feature/contract checks. The formal evaluation and documentation phases follow that prerequisite. Implementation tickets still include tests, decision records, and learner evidence as they are built.

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

## E09: documentation, in execution order

| Ticket | Work | Main learning |
| --- | --- | --- |
| L34 | Author canonical plain Markdown and select publishable pages/assets | Information architecture, evidence, links, and portable Markdown |
| L35 | Build Gatsby routes from Markdown into static HTML/assets | Filesystem sourcing, Markdown transformation, and build-time rendering |
| L36 | Copy, serve, validate, and archive the complete output | Static hosting, path prefixes, deep links, and portable artifact handoff |

L35 first checks Gatsby maintenance/security/release activity, supported Node versions, plugin engines, and a clean build; it records a dated feasibility decision before scaffolding. Gatsby remains the requested framework. If no supported compatible set works, report the blocker for an explicit scope decision.

The source is selected .md content under docs/. Gatsby configuration/layout lives under docs-site/. Use gatsby-source-filesystem and gatsby-transformer-remark; MDX is not required. The production output is the complete docs-site/public directory, including HTML, CSS, JavaScript, and images.

Node/Gatsby is required to build, not to host the resulting folder. Acceptance requires copying the full output to another location and reading it through an ordinary static HTTP server while application services and the Gatsby development server are stopped. Primary article content remains readable without JavaScript.

Root hosting and a configured-subpath build are tested separately. Copying HTML without its assets and arbitrary double-click file:// navigation are outside the portability promise. No cloud subscription, live deployment, or hosting account is needed for these tickets.

Documentation builds do not execute agents, judges, or evaluations. CI packages the static folder with source-commit/build metadata and copy/serve instructions. L27 refreshes the artifact after the final operational/security review.

## Closing stages

After E08/E09, L25 expands the existing runner for fault/load exercises, L26 reviews operational/security evidence, L27 prepares the portfolio and refreshed static documentation, and L28 completes interview practice with an independent read-only change.

Every added ticket carries pinned course references with their role explained, official documentation, implementation steps, prerequisites, scope boundaries, acceptance criteria, learning outcomes, interview questions, and an optional independent exercise.
