# 0001 - Version baseline and model provider

- Status: Proposed (L01). The versions below are **observed releases, not a restored or tested dependency set**. L03/L04 (.NET) and L17 (Python) prove them by restoring, building, and testing, then commit the lock files.
- Checked: 2026-11-09 against NuGet, PyPI, the .NET release index, python.org release data, and the MAF release tags `dotnet-1.28.0` and `python-1.25.0`.

## Selection rule

Use the **latest stable release** of every runtime, framework, and package. Use a prerelease only when no stable release of that component exists, and record why below. Provider integrations and hosting packages are versioned independently of MAF core. Do not assume they share a version.

## Runtimes and tools

| Component | Selected | Notes |
| --- | --- | --- |
| .NET SDK / runtime | 11.0.100 / 11.0.0 (STS) | Moved from 10.0.403 / 10.0.14 (LTS) in L44 (2026-11-12, the owner's choice). The official release index lists end of support for both 10.0 (LTS) and 11.0 (STS) as **2028-11-14**, so the latest-stable rule selects 11.0 at no support cost. Re-check before .NET 12 (LTS) ships. `global.json` pins the SDK (patch roll-forward only). |
| Python | 3.15.2 | The latest stable release. agent-framework-core, -anthropic, and -devui 1.25.0-era packages list 3.15 support (3.11+ required). |
| uv | 0.13.7 | Latest release. The local machine currently has 0.11.14; upgrade before L17. |
| Aspire | 13.6.5 (`Aspire.AppHost.Sdk`, `Aspire.Hosting.AppHost`, `Aspire.Hosting.Python`) | Stable. Raised from 13.6.4 in L22 (the owner's choice: in the same PR) because 13.6.4 has advisory GHSA-f7vh-h5g3-7jrh (CVE-2026-120006, high, elevation of privilege; fixed in 13.6.5). The vulnerable packages came from the `Aspire.AppHost.Sdk` version, so every Aspire pin moves together. |


## .NET packages (net11.0)

| Package | Version | Status |
| --- | --- | --- |
| Microsoft.Agents.AI | 1.28.0 | Stable |
| Microsoft.Agents.AI.Workflows | 1.28.0 | Stable |
| Microsoft.Agents.AI.Anthropic | 1.28.0 | Stable. Provider integration; depends on `Anthropic >= 12.53.0`. |
| Anthropic (C# SDK) | 13.7.0 | Stable. **Risk:** this is a newer major version than the 12.x floor MAF declares. L03 must prove it compiles and runs with MAF 1.28.0; if not, use the latest compatible 12.x and record the reason. |
| Microsoft.Extensions.AI | 10.11.0 | Stable |
| Microsoft.Extensions.AI.Evaluation | 10.11.0 | Stable. Added in L30, in the evaluation project only (with `Microsoft.Agents.AI` 1.28.0 for MAF's local evaluation). It is the version MAF 1.28.0 depends on and matches MEAI 10.11. It stays on 10.11 after the L44 move to .NET 11: the Microsoft.Extensions.AI family is versioned independently of the runtime, and moving it ahead of MAF's own dependency is out of scope. |
| Microsoft.Extensions.AI.Evaluation.Quality | 10.11.0 | Stable. Added in L32, in the evaluation project only, with the Anthropic SDK (13.7.0, the central version) for the judge client. 11.0.0 exists but depends on Microsoft.Extensions.AI.Evaluation 11, ahead of MAF 1.28's 10.11 dependency, so 10.11.0 is selected. The evaluators' prompts are documented as tuned for GPT-4o; the lane uses `claude-haiku-5-5` (the owner's choice) and checks with calibration that it still discriminates. |
| OpenTelemetry.Extensions.Hosting / Exporter.OpenTelemetryProtocol | 1.20.1 | Stable (1.21.0 is still RC). |
| Microsoft.Data.Sqlite | 11.0.0 | Stable. Added in L11 at 10.0.15, when .NET 11 was still a release candidate. Raised to 11.0.0 on 2026-11-12 (the owner's choice) once .NET 11 went GA: 11.0.0 still targets net10.0, so it runs on our runtime. It brings a major bump of the native layer (SQLitePCLRaw 2.1.13 → 3.0.5; the native library now comes from the `SQLite` 3.53.4 package). Verified by the full .NET suite (durable-state and restart tests included) and the launch-mode evaluations. |
| Microsoft.Agents.AI.DevUI | 1.28.0-preview.261106.1 | **Prerelease, unavoidable**: no stable release exists. It brings in `Microsoft.Agents.AI.Hosting` 1.28.0-preview and `Microsoft.Agents.AI.Hosting.OpenAI` 1.28.0-alpha. Restricted to the development environment (L05). |

Not selected: `Microsoft.Agents.AI.OpenAI` and `Microsoft.Extensions.AI.OpenAI` (we don't use an OpenAI provider), and the course's direct use of `Hosting.OpenAI` (`MapOpenAIResponses`). Our API serves the L02 version-1 contract instead. Hosting.OpenAI arrives only as a DevUI dependency.

## Python packages

| Package | Version | Status |
| --- | --- | --- |
| agent-framework-core | 1.25.0 | Stable. Install core plus the specific integrations; **not** the `agent-framework` meta-package, which pulls `core[all]` and many beta integrations. |
| agent-framework-anthropic | 1.0.0b261106 | **Prerelease, unavoidable**: the only MAF Claude integration for Python; no stable release exists. |
| anthropic (Python SDK) | 1.17.0 | Latest 1.x. **Not** the latest overall (2.8.0 when rechecked in L17), because agent-framework-anthropic requires `anthropic>=1.11.0,<2`. |
| agent-framework-devui | 1.0.0b261106 | **Prerelease, unavoidable**: no stable release exists. Development only. |
| opentelemetry-sdk / exporter-otlp-proto-grpc | 1.45.2 | Stable. Added in L40: OTLP over **gRPC** to Aspire (the owner's choice); brings `grpcio` 1.85.1, which ships a native cp315 wheel. **Not added:** `opentelemetry-instrumentation-fastapi` (0.66b2, prerelease), because FastAPI 0.162 has built-in, stable OpenTelemetry for HTTP spans and metrics, and adding both would duplicate them; nor `opentelemetry-instrumentation-logging` (prerelease), the suggested replacement for the SDK's `LoggingHandler`, which 1.45 deprecates. `support_agent/telemetry.py` has its own small handler on the stable logs API instead. |
| jsonschema | 4.26.0 | Stable. The Python backend validates requests and triage output against the shared contract schemas (added in L18). |
| agent-framework-core (evaluations) | 1.25.0 | The same release as the backend, added in L31 to `evaluations/python` only, for MAF's local evaluation (`evaluate_agent`, `evaluate_workflow`, `LocalEvaluator`). Core only: the evaluated agent is scripted, so no provider integration is needed. The evaluation API is **experimental** in this release (ADR 0004). |
| fastapi / uvicorn | 0.162.3 / 0.54.0 | Stable. The web framework, chosen by the owner in L17 (MAF's DevUI is built on FastAPI). |
| pydantic / pydantic-settings | 2.14.3 / 2.16.2 | Stable. Contract models and validated settings. |
| Dev tools: pytest, httpx, httpx2, ruff, pyright | 9.2.0, 0.28.1, 2.16.0, 0.17.4, 1.1.414 | Stable. pyright chosen by the owner in L17 (strict mode). httpx2 (added in L42) is the Anthropic SDK's own HTTP library; the tests use its `MockTransport` as a fake Anthropic API under the real SDK. |

## Model provider and model

- Provider: **Anthropic Claude API**, through the native MAF integrations above. We don't use an OpenAI-compatible shim.
- Default model: **`claude-haiku-5-5`**. It costs $0.10 input / $0.50 output per million tokens for prompts up to 100K tokens, and it is built for routing, classification, extraction, and sub-agent tool calls, which is this app's workload. It supports tool calling, structured output, and a 1M-token context.
- Escalation: switch to `claude-sonnet-5-5` only if a recorded live check shows Haiku failing a specific scenario (for example, triage accuracy at L07). Record the failing case. Opus is not used unless a measured need appears.
- The model ID comes from configuration (`ANTHROPIC_MODEL`) so it can be changed without a code change.
- Credentials: `ANTHROPIC_API_KEY` from environment variables or .NET user-secrets only, read only when live mode is selected. Never committed, logged, or exposed to the browser.

Constraints the implementation must respect (current Claude models):

1. **Structured output:** the Python integration maps MAF `response_format` to Claude's `output_config.format` (confirmed in the 1.25.0 source). The .NET path (the Anthropic SDK's `IChatClient`) is **not yet verified**: L05/L07 must prove typed triage output works or record the fallback.
2. **No sampling parameters:** `temperature` / `top_p` / `top_k` are rejected by current models. The Python client ignores them with a warning; don't set them in .NET.
3. **Thinking:** adaptive and on by default; effort defaults to `medium`. Don't set `budget_tokens`; the Python `ThinkingConfig` docstring still describes it, but current models reject it. Responses can start with `thinking` blocks, so read content by type, not by position, and leave headroom in `max_tokens`.
4. **Session history:** thinking blocks must be sent back unchanged in later turns. L09 must verify that MAF session storage preserves them.
5. **Refusals:** Haiku 5.5 can return `stop_reason: "refusal"` with no server-side fallback. Map it to the contract's failed outcome.
6. **No forcing a tool call:** the design uses `auto` tool mode and structured output instead of forced tool choice, which the more expensive models reject.

## Fake mode and live checks

- Fake mode is the default everywhere (CI, tests, local startup). A controllable fake chat client sits at the provider boundary, needs no key, and makes no billable calls (L03/L05, L17/L18).
- Live mode is opt-in. It requires `ANTHROPIC_API_KEY` and fails clearly if configuration is incomplete. Live smoke checks are invoked separately, limited in case count, and never part of required CI.

## Chapter 7 audit (static review of the pinned course commits)

The audit in `docs/course-audit.md` is confirmed:

- **.NET hosted path:** `Program.cs` registers four agents and the `SupportTriage` workflow (triage -> `TriageDecisionExecutor` -> conditional edge -> specialist) and exposes them through `MapOpenAIResponses`/`MapOpenAIConversations` and DevUI. `SupportOrchestrator` (injection check, refund routing) and `RefundWorkflowRunner` (refund approval) only reference each other; **neither is wired into the hosted app**. The hosted Billing agent has no refund tool.
- **.NET approval:** `RefundWorkflowRunner` answers the native approval request with a caller-supplied `customerConfirmed` flag. `OrderTools.CancelOrder` (hosted) trusts a **model-supplied** `customerConfirmed` string, so there is no real approval gate.
- **Python hosted path:** `build_runtime()` creates the same workflow and serves it through DevUI. `CancelOrder` and `ProcessRefund` use native `approval_mode="always_require"`. `CreateSupportTicket` has no approval. `SupportSecurityMiddleware` waits for the full streamed response before filtering it (a buffering/latency trade-off).
- Both: telemetry captures sensitive data (always on in .NET; opt-in through an environment variable in Python). The .NET course pins MAF 1.19.0 / DevUI 1.19.0-preview; the Python course has no dependency manifest.

## Documentation and source disagreements (observed 2026-11-09)

1. The Learn DevUI tracing page shows `tracing_enabled`; the released `serve()` in `python-1.25.0` uses `instrumentation_enabled`.
2. The Learn workflow observability page still uses `workflow_invoke`, `workflow.run`, and `workflow.name`. Both releases renamed these to `invoke_workflow {name}` and `gen_ai.workflow.name`.
3. The course's .NET `.WithOpenTelemetry(cfg => cfg.EnableSensitiveData = true)` for workflows is now an obsolete no-op (removal scheduled for 1.30.0). Workflow spans no longer carry payload attributes.

## Verification still owed

| Ticket | Check |
| --- | --- |
| L03 | **Done:** clean restore/build on SDK 10.0.403; Anthropic 13.7.0 runs with MAF 1.28.0 (`DependencyCompatibilityTests`); fake-mode startup through AppHost 13.6.4 without a key. |
| L04 | Locked restore (`packages.lock.json`), central package versions, CI builds and tests offline. |
| L05 | Agent + tool call through the Anthropic `IChatClient` with the fake client; one opt-in live smoke check on `claude-haiku-5-5`; DevUI prerelease runs in Development only. |
| L07 | .NET structured triage output with Claude; record any fallback. |
| L09 | Session history preserves thinking blocks across turns. |
| L17 | `uv lock` / `uv sync --locked` on Python 3.15.2 with core 1.25.0, anthropic integration b261106, anthropic 1.17.0; imports and fake-mode startup. |

## Sources

- MAF releases: [dotnet-1.28.0](https://github.com/microsoft/agent-framework/releases/tag/dotnet-1.28.0), [python-1.25.0](https://github.com/microsoft/agent-framework/releases/tag/python-1.25.0)
- Released source: [.NET Anthropic integration](https://github.com/microsoft/agent-framework/tree/dotnet-1.28.0/dotnet/src/Microsoft.Agents.AI.Anthropic), [Python Anthropic chat client](https://github.com/microsoft/agent-framework/blob/python-1.25.0/python/packages/anthropic/agent_framework_anthropic/_chat_client.py), [Python DevUI serve](https://github.com/microsoft/agent-framework/blob/python-1.25.0/python/packages/devui/agent_framework_devui/__init__.py)
- Package registries: [NuGet](https://www.nuget.org/packages/Microsoft.Agents.AI.Anthropic), [PyPI agent-framework-anthropic](https://pypi.org/project/agent-framework-anthropic/)
- Runtimes: [.NET release index](https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json), [Python releases](https://www.python.org/downloads/)
- Docs checked: [DevUI tracing](https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/ui/devui/tracing), [Workflow observability](https://learn.microsoft.com/en-us/agent-framework/workflows/observability)
- Course (pinned): [.NET Chapter 7](https://github.com/Dometrain/getting-started-microsoft-agent-framework-in-dotnet/tree/014ca9f4ee3764d18b259246310941a07b13541b/src/Chapter07.VoltCartSupport), [Python Chapter 7](https://github.com/Dometrain/getting-started-microsoft-agent-framework-in-python/tree/ebd05e88679a93113fe2a3223092be125752e4ba/chapter_07)
- Claude models and pricing: [Models overview](https://platform.claude.com/docs/en/about-claude/models/overview)
