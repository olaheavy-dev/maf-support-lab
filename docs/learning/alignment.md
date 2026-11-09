# Cross-ticket alignment contract

This document fixes scope and ownership between learning tickets. It is a planning constraint, not an application implementation or a completed learner decision record.

## One public specification

L02 owns version-1 HTTP, conversation, operation-status, approval, error, and stream contracts. Both backend adapters implement that specification while their native SDK internals remain idiomatic. L07 and L18 may not weaken schema validation to accommodate model output. L02 is an initial specification, not an unchangeable prediction of native SDK behavior. L10 and L12 are explicit approval/streaming feasibility checkpoints. A justified revision updates schemas, examples, contracts/README.md revision history, and every already implemented adapter/test in the same PR, with rationale and compatibility/version impact. Later implementations use that agreed revision; no revision may weaken ownership, exact-argument approval, or factual operation-state guarantees.

L22 verifies browser compatibility with the established specification. A discovered breaking change must update schemas, both adapters, and their feature/contract tests before UI work continues. L21 verifies the built application contracts; the later E08 evaluation datasets adopt that specification. Business/security failures cannot be waived as SDK differences.

Both backends use the same synthetic fixture definitions and eligibility rules. Mutable state belongs to one backend and one evaluation run; resetting one must not affect the other.

## Stage identities and writes explicitly

L03 creates a server-owned fixed development principal and a trusted test-host injection point. L05 guards order reads; L08 guards specialist reads; L09 binds conversations to their owner. L10 uses those established boundaries for approvals and writes. L13 strengthens and adversarially verifies these controls rather than introducing the first identity check.

The baseline is a local development system with synthetic users. An arbitrary client header or model argument cannot establish identity. Production identity-provider integration remains an explicit future requirement.

L05-L09 and L18 are read-only. Unresolved technical requests may suggest escalation but cannot create a ticket yet. L10 introduces simulated refund, cancellation, and support-ticket creation with bound native approval for all three; L37 implements the same rule in Python. This deliberately consistent learning baseline can later be refined through an explicit risk-policy change.

L10 uses in-memory business state; its acceptance makes no restart guarantee. L11 adds durable native state, expiry, status lookup, stable identities, and atomic operation/business-state records for every write. Python ports this design in L38, following L19 sessions and L37 approvals.

Session history, workflow checkpoints, pending approval records, and business operation records have distinct responsibilities. Persist the state actually needed by the selected SDK execution path; do not assume saving chat text restores a pending workflow.

The local simulated store must prevent a repeated approved operation from mutating state twice, including a crash after commit and before response delivery. This is not a universal exactly-once guarantee for external business providers.

## Keep stream and business outcomes separate

L02 defines the initial schemas; L12 and L20 implement the current agreed revision after the L10/L12 feasibility checks. Each turn has one final outcome: completed, awaiting_approval, failed, or cancelled. Emit one terminal event only while the connection remains usable; a disconnected client may never receive that event. Pending/final business-operation state remains authoritative and retrievable.

Awaiting approval is a pause, not successful business execution. A resumed approval is a later request bound to the same owning native session/operation. Business operation states and their enum spellings are defined once in L02, including pending, rejected/expired, running, committed, and failed cases.

A dropped stream does not roll back a committed operation. Status lookup and duplicate-operation protection reconcile that case. Cancellation is cooperative: propagate it, observe it at a defined pre-commit boundary, stop launching new work when observed, and report factual committed state. Connection loss, cancellation notification, and commit can race. A disconnect alone does not guarantee prevention of a later commit, and an upstream provider is not guaranteed to stop computing.

Refresh recovery requires retrieving pending/final state. Replaying every model-token delta is outside the baseline. Choose output buffering deliberately and measure first-output as well as final-response latency.

## Instrument and test in stages

L03 establishes explicit fake-mode startup without credentials. L04 adds real .NET build/integration checks; L05 introduces a controllable fake model/client. Live-mode configuration and smoke runs are explicit and separately bounded.

L06 proves HTTP/agent/model/tool traces, logs, and actual metric samples reach Aspire. L07 extends that chain with workflow/executor spans. The presence of an AppHost or DevUI does not prove all three telemetry signals are configured.

Use bounded metric dimensions such as route, tool, and outcome. Conversation/operation identifiers belong in appropriately protected traces/logs, not unbounded metric labels. Configure telemetry ownership once and avoid double-counting the same provider usage. Missing usage is unknown, not zero; pricing-based estimates are separate from provider bills.

L15 inventories provider, HTTP, and application retries and bounds total attempts, tool/agent steps, output size, and turn duration. Python must port these controls in L42; they cannot be deferred beyond parity.

The repository-checks job remains the stable required CI aggregate. Existing language checks must feed it as introduced. Python packaging/test gates join in L17; frontend gates join in L23/L24. No required offline job needs model credentials.

L21 verifies the built backend projects using their existing integration/contract checks. L24 completes both APIs and the shared frontend and produces application_projects_built. Only then does E08 begin: L29 defines evaluation projects/datasets, L16 implements the shared deterministic runner for both targets, L30/L31 demonstrate native Microsoft .NET/Python evaluators, L32 adds budgeted quality scoring, and L33 generates reports and separate CI lanes. L25 later expands that existing runner for fault/load evidence.

## Python parity ownership

| .NET behavior | .NET owner | Python owner |
| --- | --- | --- |
| Host, fake mode, trusted development principal | L03-L05 | L17 |
| Read-only agents, scoped tools, validated routing, DevUI | L05/L07/L08 | L18 |
| Conversation isolation | L09 | L19 |
| Approval and eligibility | L10 | L37 |
| Durable native state and atomic operation records | L11 | L38 |
| Customer/conversation/operation access matrix | L13 | L39 |
| Streaming and cooperative cancellation | L12 | L20 |
| OTLP traces, logs, and metrics in Aspire | L06/L07 | L40 |
| Injection and tool/output boundaries | L14 | L41 |
| Retry, timeout, step, and output budgets | L15 | L42 |
| Application feature/contract integration | L04 onward | L17 onward, verified together in L21 |
| Post-build formal evaluation | L29/L16/L30 | L29/L16/L31; shared scoring/reporting in L32/L33 |

The Python sequence is L19 -> L37 -> L38 -> L39 -> L20 -> L40 -> L41 -> L42, with one focused learning issue per slice and a native blocking chain. Existing L19/L20 issue IDs remain stable; their other capabilities move to the named tickets. L21 cannot start until every Python parity capability is present.

## Frontend and final review

L23 uses relative frontend API calls through a server/development proxy configured from Aspire-provided URLs. The browser does not resolve internal Aspire service names. Backend selection is a server-controlled alias fixed for the conversation, never an arbitrary user-provided upstream URL.

L24 builds approval/recovery controls over existing operation identities and adds browser-level checks for both backends. UI disabled states do not replace server authorization or idempotency.

L02 starts the threat model, L13/L14 extend it, and L26 reviews it. L26 is a final review of existing protections, not the first dependency/security check. L27 distinguishes a self-check from another person's independent run.

L28's independent change is one read-only return-eligibility query in both implementations using the existing authoritative policy. It adds evaluation coverage without changing refund authorization or introducing another write.

Stretch exercises are optional and may not introduce a prerequisite from a future ticket. Learner explanations and evidence remain the learner's work.

## Post-build evaluation and Gatsby documentation

Execution order is explicit in the roadmap and GitHub Project Order field. Stable IDs and issue numbers are preserved: L16 now runs after L29 and application completion, not before Python. New prerequisites are checked by capabilities and native blocking relationships.

Unit/integration tests and learner notes remain alongside feature work. Formal evaluation projects, datasets, quality judges, reports, and the documentation-site project begin after both APIs and the frontend are built.

Evaluation dependencies belong under evaluations/dotnet/Support.Evaluations and evaluations/python, outside production API manifests. The same captured evidence/dataset/result specification feeds both targets. Local evaluators can avoid judge calls while response generation can still call a model; fake or recorded responses keep the required lane credential-free. Quality scores never replace ownership/approval/idempotency assertions. Reporting adapters for native/custom results must be verified against actual supported APIs.

E09 is explicitly Gatsby. L35 first records a dated maintenance/security, supported Node, and plugin compatibility check plus a clean-build feasibility result; an unsupported combination requires a scope decision rather than a silent framework substitution. L34 authors selected plain .md pages under docs/ and their publication manifest. L35 uses gatsby-source-filesystem and gatsby-transformer-remark to generate routes and static HTML in docs-site/public. There is no Docusaurus or MDX-only authoring requirement. The whole output folder includes HTML, CSS, JavaScript, and image assets; Node/Gatsby is a build-time requirement, not a hosting requirement.

L36 copies the complete output elsewhere, checks direct/deep links through an ordinary static HTTP server, verifies root and configured-subpath builds, and packages a downloadable artifact with copy/serve instructions. This does not promise arbitrary file:// support or a single self-contained HTML file, and requires no deployment or hosting account. Documentation builds do not run agents, judges, or evaluations. L27 refreshes that artifact after final operational/security changes.
