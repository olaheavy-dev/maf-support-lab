# Ordered learning roadmap

Start with L01. Only one implementation ticket is in progress. Each ticket includes exact source links, implementation steps, verification, learning outcomes, and interview questions.

| Order | Epic | Ticket | Stack | Depends on |
| --- | --- | --- | --- | --- |
| L01 | E01 | [Audit Chapter 7 and record a verified version baseline](tickets/L01.md) | shared | None |
| L02 | E01 | [Define shared support contracts and synthetic fixtures](tickets/L02.md) | shared | L01 |
| L03 | E01 | [Scaffold .NET support API and C# Aspire AppHost](tickets/L03.md) | dotnet | L02 |
| L04 | E01 | [Add reproducible builds and meaningful offline CI](tickets/L04.md) | dotnet | L03 |
| L05 | E02 | [Build the first .NET order-status agent and DevUI](tickets/L05.md) | dotnet | L04 |
| L06 | E02 | [Export traces, metrics, and structured logs to Aspire](tickets/L06.md) | dotnet | L05 |
| L07 | E02 | [Implement validated triage and deterministic routing](tickets/L07.md) | dotnet | L06 |
| L08 | E02 | [Complete .NET billing and technical specialists](tickets/L08.md) | dotnet | L07 |
| L09 | E03 | [Add per-conversation sessions and preserve context](tickets/L09.md) | dotnet | L08 |
| L10 | E03 | [Require bound approval for refunds and cancellations](tickets/L10.md) | dotnet | L09 |
| L11 | E03 | [Persist pending approvals and prevent duplicate operations](tickets/L11.md) | dotnet | L10 |
| L12 | E03 | [Implement streaming, cancellation, and terminal events](tickets/L12.md) | dotnet | L11 |
| L13 | E04 | [Enforce customer access and conversation ownership](tickets/L13.md) | dotnet | L12 |
| L14 | E04 | [Validate tool boundaries and exercise prompt injection](tickets/L14.md) | dotnet | L13 |
| L15 | E04 | [Add bounded timeouts, retries, and graceful failure](tickets/L15.md) | dotnet | L14 |
| L16 | E04 | [Establish behavioral, latency, and usage baselines](tickets/L16.md) | shared | L15 |
| L17 | E05 | [Scaffold Python packaging, health, and Aspire integration](tickets/L17.md) | python | L16 |
| L18 | E05 | [Implement Python agents, typed tools, and triage](tickets/L18.md) | python | L17 |
| L19 | E05 | [Implement Python sessions, approval persistence, and idempotency](tickets/L19.md) | python | L18 |
| L20 | E05 | [Implement Python streaming and correlated telemetry](tickets/L20.md) | python | L19 |
| L21 | E05 | [Run cross-language parity checks and document SDK decisions](tickets/L21.md) | shared | L20 |
| L22 | E06 | [Finalize the frontend-facing protocol and stream/approval lifecycle](tickets/L22.md) | frontend | L21 |
| L23 | E06 | [Build shared chat UI with backend selection](tickets/L23.md) | frontend | L22 |
| L24 | E06 | [Add approval UI, recovery states, and trace references](tickets/L24.md) | frontend | L23 |
| L25 | E07 | [Run fault, concurrency, and bounded load exercises](tickets/L25.md) | shared | L24 |
| L26 | E07 | [Review dependencies, security checks, and operational limits](tickets/L26.md) | shared | L25 |
| L27 | E07 | [Publish the architecture, runbook, and portfolio demonstration](tickets/L27.md) | shared | L26 |
| L28 | E07 | [Complete a mock interview and independent change exercise](tickets/L28.md) | shared | L27 |

## Epics

### E01: Foundation, contracts, and reproducibility

Explain the architecture, freeze a verified baseline, and start a buildable .NET/Aspire skeleton.

Exit: A fresh checkout restores, builds, and starts the skeleton; the API and synthetic data contracts are documented.

### E02: Observable .NET support workflow

Implement the Chapter 7 request path with working tools, routing, DevUI, and telemetry.

Exit: Demonstrate orders, billing, and technical requests and follow a request from HTTP entry through model/tool spans.

### E03: Conversations, approvals, and streaming

Preserve conversation state and safely pause/resume side-effecting work.

Exit: Demonstrate a follow-up, approval rejection, approved refund, restart/resume, cancellation, and streamed error.

### E04: Safety, reliability, and measurement

Enforce business boundaries and measure behavior and resource use.

Exit: Show unauthorized access rejection, bounded failure handling, and a baseline evaluation with honest cost/quality limits.

### E05: Python implementation and behavioral parity

Rebuild the complete behavior idiomatically in Python and compare SDK choices.

Exit: The shared evaluation runs against both backends with isolated state and comparable telemetry.

### E06: One frontend for both implementations

Add a browser experience over the shared backend contract.

Exit: Select a backend when starting a conversation, chat, approve/reject an operation, and diagnose a failure.

### E07: Engineering evidence and interview readiness

Demonstrate the system, defend decisions, and state its operational limits.

Exit: Complete a fault demonstration, publish a reproducible runbook, and answer a recorded or written mock interview.
