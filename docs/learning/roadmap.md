# Ordered learning roadmap

Work one ticket at a time. Stable ticket IDs and issue links are retained. Only the first unblocked ticket is Ready.

The [alignment contract](alignment.md) fixes cross-ticket scope and the [capability map](capabilities.md) identifies prerequisites. Source metadata lives in backlog.json and ../sources.json; generated ticket/epic pages must be rerendered when it changes.

| Order | Epic | Learning ticket | Stack | Depends on |
| --- | --- | --- | --- | --- |
| L01 | E01 | [Audit Chapter 7 and record a verified version baseline](tickets/L01.md) | shared | None |
| L02 | E01 | [Define shared support contracts and synthetic fixtures](tickets/L02.md) | shared | L01 |
| L03 | E01 | [Scaffold .NET support API and C# Aspire AppHost](tickets/L03.md) | dotnet | L02 |
| L04 | E01 | [Add reproducible builds and meaningful offline CI](tickets/L04.md) | dotnet | L03 |
| L05 | E02 | [Build the first .NET order-status agent and DevUI](tickets/L05.md) | dotnet | L04 |
| L06 | E02 | [Export traces, metrics, and structured logs to Aspire](tickets/L06.md) | dotnet | L05 |
| L07 | E02 | [Implement validated triage and deterministic routing](tickets/L07.md) | dotnet | L06 |
| L08 | E02 | [Complete read-only .NET billing and technical specialists](tickets/L08.md) | dotnet | L07 |
| L09 | E03 | [Add per-conversation sessions and preserve context](tickets/L09.md) | dotnet | L08 |
| L10 | E03 | [Require bound approval for simulated support mutations](tickets/L10.md) | dotnet | L09 |
| L11 | E03 | [Persist pending approvals and prevent duplicate operations](tickets/L11.md) | dotnet | L10 |
| L12 | E03 | [Implement streaming, cancellation, and terminal events](tickets/L12.md) | dotnet | L11 |
| L13 | E04 | [Verify customer access and conversation ownership boundaries](tickets/L13.md) | dotnet | L12 |
| L14 | E04 | [Validate tool boundaries and exercise prompt injection](tickets/L14.md) | dotnet | L13 |
| L15 | E04 | [Add bounded timeouts, retries, and graceful failure](tickets/L15.md) | dotnet | L14 |
| L16 | E04 | [Establish behavioral, latency, and usage baselines](tickets/L16.md) | shared | L15 |
| L17 | E05 | [Scaffold Python packaging, health, and Aspire integration](tickets/L17.md) | python | L16 |
| L18 | E05 | [Implement Python read-only agents, tools, and triage](tickets/L18.md) | python | L17 |
| L19 | E05 | [Implement Python sessions, approval persistence, and idempotency](tickets/L19.md) | python | L18 |
| L20 | E05 | [Complete Python streaming, telemetry, and resilience](tickets/L20.md) | python | L19 |
| L21 | E05 | [Run cross-language parity checks and document SDK decisions](tickets/L21.md) | shared | L20 |
| L22 | E06 | [Verify browser compatibility with the established API contract](tickets/L22.md) | frontend | L21 |
| L23 | E06 | [Build shared chat UI with backend selection](tickets/L23.md) | frontend | L22 |
| L24 | E06 | [Add approval UI, recovery states, and trace references](tickets/L24.md) | frontend | L23 |
| L25 | E07 | [Run fault, concurrency, and bounded load exercises](tickets/L25.md) | shared | L24 |
| L26 | E07 | [Review dependencies, security checks, and operational limits](tickets/L26.md) | shared | L25 |
| L27 | E07 | [Publish the architecture, runbook, and portfolio demonstration](tickets/L27.md) | shared | L26 |
| L28 | E07 | [Complete a mock interview and independent change exercise](tickets/L28.md) | shared | L27 |

## Epics

### [E01: Foundation, contracts, and reproducibility](epics/E01.md)

Explain the architecture, freeze a verified baseline, and start a buildable .NET/Aspire skeleton.

Exit: A fresh checkout restores, builds, and starts the skeleton; the API and synthetic data contracts are documented.

### [E02: Observable .NET support workflow](epics/E02.md)

Implement the read-only Chapter 7 support path with guarded tools, routing, DevUI, and telemetry.

Exit: Demonstrate read-only orders, billing, and technical requests with correlated workflow/model/tool telemetry; mutating tools remain unregistered until E03.

### [E03: Conversations, approvals, and streaming](epics/E03.md)

Preserve conversation state and safely pause/resume side-effecting work.

Exit: Demonstrate isolated follow-ups, bound approval/rejection for all simulated writes, restart/resume, duplicate protection, and the shared streaming terminal outcomes.

### [E04: Safety, reliability, and measurement](epics/E04.md)

Strengthen and verify established business boundaries, bound failures, and measure behavior/resource use.

Exit: Show unauthorized access rejection, bounded failure handling, and a baseline evaluation with honest cost/quality limits.

### [E05: Python implementation and behavioral parity](epics/E05.md)

Rebuild the complete behavior idiomatically in Python and compare SDK choices.

Exit: Both backends pass the same deterministic contracts, ownership, approval, durable-state, streaming, resilience, and injection-boundary scenarios with isolated state and comparable telemetry.

### [E06: One frontend for both implementations](epics/E06.md)

Verify browser compatibility with the established backend contract and add one shared frontend.

Exit: Select a backend when starting a conversation, chat, approve/reject an operation, and diagnose a failure.

### [E07: Engineering evidence and interview readiness](epics/E07.md)

Demonstrate the system, defend decisions, and state its operational limits.

Exit: Complete a fault demonstration, publish a reproducible runbook, and answer a recorded or written mock interview.
