# Ticket alignment review - updated 2026-11-09

The current plan contains nine epics and 42 learning tickets. This report supersedes the original seven-epic, 28-ticket review and the later 36-ticket expansion. No application work or learner understanding is marked complete.

## Current review corrections

| Finding | Correction |
| --- | --- |
| Review counts and evaluation ownership described the original plan | L21 verifies existing application feature/contracts only. After L24 builds both APIs and the frontend, L29 sets up evaluation projects and L16 runs the shared deterministic harness against both backends. |
| L14/L24 listed evaluation deliverables before the post-build phase | Keep L14 adversarial fixtures/checks in Support.Api.Tests and L24 browser checks in frontend/tests; formal evaluation projects/datasets begin at L29. |
| ADR numbering differed from authoring sequence | L01 owns 0001-version-baseline, L43 writes 0002-code-layout, L11 writes 0003-state-and-idempotency, and L21 writes 0004-sdk-comparison. L35 writes 0005-gatsby-build-feasibility. L38 extends the existing state ADR with Python evidence. |
| L19/L20 combined too many independent learning goals | Retain their stable IDs for sessions/streaming. Add L37 approvals, L38 durability, L39 access checks, L40 OTLP telemetry, L41 injection boundaries, and L42 resilience, each with references, outcomes, interview practice, evidence, and native blocking links. |
| L02 could freeze contracts before native approval/streaming experience | Make L10/L12 explicit feasibility/revision checkpoints. Update schemas, examples, revision history, implemented adapters and tests together; later backends/UI use the agreed revision. |
| Gatsby maintenance/Node support could be assumed | L35 records dated maintenance/security/release evidence, supported Node/plugin compatibility, and a clean-build result before accepting the dependency set. |

## Earlier corrections retained

- Trusted development identity starts at L03, and ownership guards accompany each read/session/write. L13/L39 verify the full access matrix.
- Fake-mode startup and offline checks precede model calls. Live-mode configuration and usage are explicit and bounded.
- L08/L18 stay read-only; L10/L37 introduce approval-bound simulated refund, cancellation, and ticket creation.
- L11/L38 require atomic local business/operation records separately from native checkpoints and duplicate protection. No universal exactly-once external-provider claim is made.
- Stream delivery, cooperative cancellation, approval pauses, and factual business-operation state remain distinct. A disconnected client may receive no terminal event; a committed action is not rolled back by disconnect.
- Operational metrics remain distinct from model-quality evaluation. Missing usage is unknown and pricing-based cost is an estimate.
- Browser calls use a controlled proxy/backend alias, fixed per conversation; server-owned endpoints and credentials stay outside browser code.
- Generated tickets, epic pages, roadmap, and capability ownership come from canonical metadata and are validated in full.

## Verification and limits

The review checks source roles, execution order, scope boundaries, capability ownership, contract revision policy, and durable decision paths. Repository regression checks reject pre-build evaluation deliverables and out-of-order numbered ADRs as well as the existing dependency/generated-document failures.

Publishing must synchronize issue text, native epic/sub-issue and blocking relationships, project Order/positions, and the generated roadmap while preserving actual state, assignees, comments, completed checkboxes, and learner evidence. The publication verifier checks these against the pre-change snapshot.

Planning/source checks do not establish runtime or provider compatibility. L01 selects a baseline; L03/L04 and L17 prove builds/imports; L35 proves the Gatsby build. Follow execution Order, not numerical ticket IDs.
