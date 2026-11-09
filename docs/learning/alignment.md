# Cross-ticket alignment contract

This document fixes scope and ownership between learning tickets. It is a planning constraint, not an application implementation or a completed learner decision record.

## One public specification

L02 owns version-1 HTTP, conversation, operation-status, approval, error, and stream contracts. Both backend adapters implement that specification while their native SDK internals remain idiomatic. L07 and L18 may not weaken schema validation to accommodate model output.

L22 verifies browser compatibility with the established specification. A discovered breaking change must update schemas, both adapters, and shared evaluations before UI work continues. L21 business/security failures cannot be waived as SDK differences.

Both backends use the same synthetic fixture definitions and eligibility rules. Mutable state belongs to one backend and one evaluation run; resetting one must not affect the other.

## Stage identities and writes explicitly

L03 creates a server-owned fixed development principal and a trusted test-host injection point. L05 guards order reads; L08 guards specialist reads; L09 binds conversations to their owner. L10 uses those established boundaries for approvals and writes. L13 strengthens and adversarially verifies these controls rather than introducing the first identity check.

The baseline is a local development system with synthetic users. An arbitrary client header or model argument cannot establish identity. Production identity-provider integration remains an explicit future requirement.

L05-L09 and L18 are read-only. Unresolved technical requests may suggest escalation but cannot create a ticket yet. L10 introduces simulated refund, cancellation, and support-ticket creation with bound native approval for all three; L19 implements the same rule in Python. This deliberately consistent learning baseline can later be refined through an explicit risk-policy change.

L10 uses in-memory business state; its acceptance makes no restart guarantee. L11 adds durable native state, expiry, status lookup, stable identities, and atomic operation/business-state records for every write. Python ports this design in L19.

Session history, workflow checkpoints, pending approval records, and business operation records have distinct responsibilities. Persist the state actually needed by the selected SDK execution path; do not assume saving chat text restores a pending workflow.

The local simulated store must prevent a repeated approved operation from mutating state twice, including a crash after commit and before response delivery. This is not a universal exactly-once guarantee for external business providers.

## Keep stream and business outcomes separate

L02 defines the exact schemas; L12 and L20 implement them. Each connected turn has one terminal transport outcome: completed, awaiting_approval, failed, or cancelled.

Awaiting approval is a pause, not successful business execution. A resumed approval is a later request bound to the same owning native session/operation. Business operation states and their enum spellings are defined once in L02, including pending, rejected/expired, running, committed, and failed cases.

A dropped stream does not roll back a committed operation. Status lookup and duplicate-operation protection reconcile that case. Cancellation is cooperative: propagate it, stop launching new work, and report factual committed state. Do not claim an upstream provider necessarily stopped computing.

Refresh recovery requires retrieving pending/final state. Replaying every model-token delta is outside the baseline. Choose output buffering deliberately and measure first-output as well as final-response latency.

## Instrument and test in stages

L03 establishes explicit fake-mode startup without credentials. L04 adds real .NET build/integration checks; L05 introduces a controllable fake model/client. Live-mode configuration and smoke runs are explicit and separately bounded.

L06 proves HTTP/agent/model/tool traces, logs, and actual metric samples reach Aspire. L07 extends that chain with workflow/executor spans. The presence of an AppHost or DevUI does not prove all three telemetry signals are configured.

Use bounded metric dimensions such as route, tool, and outcome. Conversation/operation identifiers belong in appropriately protected traces/logs, not unbounded metric labels. Configure telemetry ownership once and avoid double-counting the same provider usage. Missing usage is unknown, not zero; pricing-based estimates are separate from provider bills.

L15 inventories provider, HTTP, and application retries and bounds total attempts, tool/agent steps, output size, and turn duration. Python must port these controls in L20; they cannot be deferred beyond parity.

The repository-checks job remains the stable required CI aggregate. Existing language checks must feed it as introduced. Python packaging/test gates join in L17; frontend gates join in L23/L24. No required offline job needs model credentials.

L16 creates the endpoint-configurable shared evaluation harness and records the .NET baseline. L21 runs the same behavioral/security cases against both backends. L25 expands fault/concurrency/load evidence rather than introducing a second incompatible harness.

## Python parity ownership

| .NET behavior | .NET owner | Python owner |
| --- | --- | --- |
| Host, fake mode, trusted development principal | L03-L05 | L17 |
| Read-only agents, scoped tools, validated routing, DevUI | L05/L07/L08 | L18 |
| Conversation isolation, approval, eligibility, durable operations, ownership matrix | L09-L11/L13 | L19 |
| Streaming/cancellation, telemetry, boundary-validation fixtures, retry/step/output budgets | L06/L12/L14/L15 | L20 |
| Common regression/evaluation contract | L16 | L21 for both targets |

L19 and L20 are porting slices across already explored concepts. Work their listed implementation steps separately; if a slice is too large, split it into linked sub-tasks before implementation while preserving the parent acceptance criteria. Do not silently omit a behavior to fit a time estimate.

## Frontend and final review

L23 uses relative frontend API calls through a server/development proxy configured from Aspire-provided URLs. The browser does not resolve internal Aspire service names. Backend selection is a server-controlled alias fixed for the conversation, never an arbitrary user-provided upstream URL.

L24 builds approval/recovery controls over existing operation identities and adds browser-level checks for both backends. UI disabled states do not replace server authorization or idempotency.

L02 starts the threat model, L13/L14 extend it, and L26 reviews it. L26 is a final review of existing protections, not the first dependency/security check. L27 distinguishes a self-check from another person's independent run.

L28's independent change is one read-only return-eligibility query in both implementations using the existing authoritative policy. It adds evaluation coverage without changing refund authorization or introducing another write.

Stretch exercises are optional and may not introduce a prerequisite from a future ticket. Learner explanations and evidence remain the learner's work.
