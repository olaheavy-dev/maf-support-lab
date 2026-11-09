# Chapter 7 starting-point audit

Checked 2026-11-09 against the course commits recorded in sources.json and the official MAF .NET 1.28.0 / Python 1.25.0 releases. This is static source review, not execution of the course projects or access to private lesson transcripts.

Keep the triage/orders/billing/technical use case, scoped tool sets, structured triage, human approval concepts, and OpenTelemetry.

Address these differences during the learning tickets:

1. .NET Program.cs hosts a workflow directly; the separate SupportOrchestrator security/refund path is not wired into it.
2. The cancellation tool accepts a model-supplied customerConfirmed string; framework-bound approval must be enforced by the host/tool path.
3. .NET startup explicitly configures tracing, without an explicit metrics export pipeline.
4. Specialists receive generated summaries. Preserve original request context and define per-conversation session ownership.
5. The C# specialist calls RunAsync; Python security middleware waits for the completed response. Buffering is a policy/latency decision.
6. Refunds and ticket operations are mock success strings; eligibility, authorization, persistent state, and duplicate-operation prevention need explicit design.
7. .NET enables sensitive telemetry at multiple layers. Current Python DevUI instrumentation can also opt into sensitive capture.
8. The course .NET manifest uses older MAF versions; the Python repository lacks a visible dependency manifest/lockfile.

Documentation drift found:

- Recent workflow migration notes use invoke_workflow and gen_ai.workflow.name, while the Learn workflow observability page still shows older names.
- The Learn DevUI tracing page shows tracing_enabled; the released Python serve signature uses instrumentation_enabled.
- DevUI/hosting packages can be prerelease even while core packages are released.

Use sources.json for exact course files, release notes, documentation, and tagged official examples. L01 verifies this audit before application work.
