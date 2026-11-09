# Ticket alignment review - 2026-11-09

Reviewed all seven epics and all 28 learning tickets against their predecessor sequence, shared contracts, course reference roles, official documentation, expected learning outcomes, and interview questions. No application behavior was implemented or marked learned.

## Findings corrected

| Finding | Correction |
| --- | --- |
| L08 described a read-only task while requiring ticket creation | Keep L08 read-only; introduce all three simulated writes with approval in L10. |
| L10 required customer authorization before L13 first introduced identity | Establish the trusted development boundary in L03, guard reads/sessions as introduced, and make L13 a comprehensive verification task. |
| Health/CI startup could require credentials before an offline mode existed | Make fake-mode startup explicit in L03 and Python L17; validate live configuration only when selected. |
| L07 expected all domains to operate before L08 implemented specialists | Route tests use explicit stubs; only read-only specialists become complete in L08. |
| Workflow telemetry appeared to be required before a workflow existed | Prove agent/tool signals in L06; add workflow/executor correlation in L07. |
| The contract was finalized after both backends had been implemented | L02 owns the protocol; L22 verifies browser compatibility and requires parity-preserving updates for any breaking discovery. |
| Persistence and duplicate protection focused on refunds | Cover refunds, cancellations, and ticket creation, including commit/response-loss recovery and the limits of local guarantees. |
| Cancellation and approval pauses lacked precise transport semantics | Define one terminal transport outcome and distinguish paused, committed, disconnected, and cancelled cases. |
| Python parity did not explicitly include .NET authorization, validation, retry, and budget behavior | Assign those porting responsibilities to L17-L20 and make L21's acceptance require the complete common invariant set. |
| Evaluation/operational metrics could be mixed or imply Python existed at L16 | L16 owns a reusable harness and .NET baseline; L21 proves both targets; missing usage remains unknown and cost remains an estimate. |
| Frontend endpoint configuration could imply browser access to internal service discovery | Require a server/dev proxy and server-controlled backend aliases with fixed per-conversation ownership. |
| L26 updated a threat model that no earlier ticket created | Start it in L02, extend it in L13/L14, review it in L26. |
| Readable tickets could drift from backlog JSON because validation only checked links | Add one deterministic renderer and validate full generated content, capability prerequisites, stable issue mapping, and metadata coherence. |

## Verification scope

- Every original registered reference URL resolved, including commit-pinned course files. Four targeted official references were added for integration tests, HTTP resilience, Aspire telemetry, and dependency-update configuration and checked separately.
- Existing ticket/epic IDs, issue numbers, parent relationships, order, states, assignees, and learning progress are preserved.
- Repository validation checks all generated ticket/epic pages, the roadmap, and the capability map against canonical sources.
- Focused negative checks ensure a future capability dependency, changed acceptance text, or invalid source classification cannot silently pass validation.
- Source existence checks do not prove an SDK combination compiles. L01/L03/L04/L17 retain that verification responsibility.

## Remaining learner decisions

L01 selects a compatible runtime/provider/model/package baseline. L02 writes the concrete version-1 schemas/routes and authoritative fixture policies. L11 selects a suitable supported local durable store. L23 selects the frontend stack. These are intentional learning decisions, with ownership and acceptance boundaries now explicit.

## Subsequent phase expansion

This report records the earlier 28-ticket alignment review. The current 36-ticket plan, including reordered formal evaluation and Gatsby documentation phases, is described in post-build-plan.md and the generated roadmap.
