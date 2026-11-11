# 0004 - SDK comparison: one contract, two native implementations

- Status: Accepted (L21, 2026-11-11).
- Versions: MAF .NET 1.28.0 with the Anthropic C# SDK 13.7.0, and MAF Python 1.25.0 with `agent-framework-anthropic` 1.0.0b261106 and the Anthropic Python SDK 1.17.0 ([0001](0001-version-baseline.md)).

## Decision

Both backends implement the same version-1 contract (`contracts/`) with each language's **native** MAF SDK and idioms. We don't translate one into the other. They must agree on everything a client can observe:
- status codes, problem codes and `errors[]`;
- turn outcomes, routes and reply kinds;
- operation states;
- ownership and approval behaviour;
- the stream framing and event order.

They may differ in the wording of human-readable text, and in how they get there internally. A business or security rule is never accepted as an "SDK difference".

## Native choices per stack

| Concern | .NET | Python |
| --- | --- | --- |
| Agents and workflow | `ChatClientAgent`s; `WorkflowBuilder` with `AddSwitch`; `InProcessExecution.RunStreamingAsync` | `Agent`s; `WorkflowBuilder.add_switch_case_edge_group`; `workflow.run(stream=True)` |
| Model client | Anthropic SDK `IChatClient` (`AsIChatClient`), wrapped by `BudgetedChatClient` and OpenTelemetry | MAF `AnthropicClient` over an `AsyncAnthropic` client the app builds, with chat and function middleware on every agent |
| Approval | `ApprovalRequiredAIFunction` → `ToolApprovalRequestContent` / `CreateResponse(approved, reason)` | `@tool(approval_mode="always_require")` → `function_approval_request` / `to_function_approval_response()` |
| Sessions and history | `AgentSession`, `SerializeSessionAsync`, own `BoundedHistoryReducer` | `AgentSession`, a `BoundedHistory` context provider (`InMemoryHistoryProvider` subclass) |
| Contract validation | Hand-written validators (System.Text.Json and regexes) in Presentation; triage output against the full schema | `jsonschema` against the contract schema files themselves |
| Web host and streaming | ASP.NET Core minimal APIs; `TypedResults.ServerSentEvents` | FastAPI; `EventSourceResponse` |
| Code layout | One project: modules × layers, CQRS handlers, ArchUnitNET rules ([0002](0002-code-layout.md)) | Flat modules by concern; a ruff rule forbids importing .NET code |

## Resource lifecycle

| Resource | .NET | Python |
| --- | --- | --- |
| Model client | DI singleton. The SDK's `HttpClient` is created directly, not by `IHttpClientFactory`, so the service defaults' resilience handler can't add a second retry layer (L15). | Created once in the FastAPI lifespan and registered with `Resources`, which closes clients newest first. MAF won't close an SDK client it was given, so the app closes it (L42). |
| Database | `SupportDatabase` singleton, one file per backend and run | `Database`, opened at startup and closed (checkpointed) at shutdown |
| Running turns | Detached `Task`s with their own `CancellationTokenSource`; host shutdown cancels them | `RunningTurns`: detached tasks; shutdown cancels them and waits for them to be stored before the database closes |
| Telemetry | Aspire `ServiceDefaults`; host-owned providers | Its own providers, once per process; flushed in the lifespan; a bounded 5 s exit (L40) |
| DevUI | Same process, Development only | A separate process: a mounted sub-app's lifespan doesn't run, and DevUI calls `/v1` at the root |

## Known implementation differences (none is client-visible except as noted)

| Area | .NET | Python | Effect on the contract |
| --- | --- | --- | --- |
| Rejection reason | Reaches the model with the native rejection | Audit-only: MAF Python's approval response has no reason field | None. The reason is never trusted either way (L41). |
| Tool call/result pairing | The native session keeps the tool call | History is plain text, so the gate re-sends the call (or the whole parallel batch) before answering | None. Found live in L37/L20; the fake model now enforces Anthropic's pairing rule. |
| Cancellation | `CancellationToken` everywhere | A flag plus `task.cancel()`, only for a started task. `TurnCancelled` subclasses `CancelledError` because MAF's tool layer absorbs ordinary exceptions. | None. Both have the same pre-commit boundary. |
| Tool fault ends the turn | `MaximumConsecutiveErrorsPerRequest = 0` | `ToolLimits` raises `MiddlewareFailure`; only MAF's argument validation goes back to the model | None (`tool_unavailable` in both). |
| Tool timeout | `FunctionInvoker` with a timeout | `asyncio.timeout`. A sync tool runs in a worker thread, which finishes in the background (read-only lookups only). | None. |
| Rate-limit hint | A `DelegatingHandler` captures `retry-after`, because the SDK's exception lacks it | Read from `RateLimitError.response` | None (`retry_after_seconds` in both). |
| Validation messages | Its own wording; one `errors[]` entry for the first problem | jsonschema's wording; possibly several entries | **Wording only.** Both always list errors with JSON pointers, and `""` is the whole body (aligned in L21). |
| Fake models | `FakeChatClient` | `fake_model.py`, a port | Reply **wording** differs; routes, kinds, facts and ids match. |
| Tool argument names | C# parameter names (`orderId`, `relatedOrderId`): MEAI's `AIFunctionFactory` keeps them | snake_case (`order_id`) | None for clients (arguments never cross the API). Evaluation datasets store canonical snake_case, and each native adapter maps it (L30). |
| Approval-required calls in native evaluation | A paused write arrives as a `ToolApprovalRequestContent`. MAF 1.28.0's `EvalChecks.ToolCallArgsMatch` counts only `FunctionCallContent`, so in a live `EvaluateAsync(queries, …)` run it reports the write as "not called". | (L31) | None for clients. Captures keep the call as `approval_request`, and the recorded evaluation gives it to the checks as the model's call (L30). |

## Compatibility evidence (L21)

- **Each backend's own feature and contract tests:** .NET 246 passed (10 live tests skipped); Python 266 passed (14 live tests skipped). Both run the same 19 shared scenarios (`fixtures/scenarios`), with no model credentials.
- **Cross-backend probes** (`contracts/probes/run_probes.py`, the `cross-backend` CI job):
  - They start the real .NET and Python servers in fake mode, two processes each (one per customer) on one SQLite file.
  - They send the same 29 requests to both, validate every response against the schemas, and compare the normalized results.
  - Result: **they agree on all 29**.
- **The probes found three drifts, all fixed:**
  1. A failed turn had a `route` in .NET but none in Python. Python now keeps the route triage chose.
  2. .NET's invalid create body had no `errors[]`, although the contract requires it. It now lists the offending pointer.
  3. Python used `"/"` for the whole body. It now uses `""` (RFC 6901).
- **The probes catch regressions:** removing Python's ownership check, or changing .NET's `client_turn_id_reused` status, each fails them (evidence: `docs/learning/evidence/L21.md`).
- **Out of scope here:** model quality, statistical comparison and formal evaluation wait for E08 (L29+). These probes assert contract behaviour on synthetic fixtures, not answer quality.

## Choosing an SDK for a team

- **Both are viable for this workload.** The contract, approval, durability and safety guarantees were reached in both.
- **.NET's strengths:**
  - A more complete native surface for this app: the approval reason reaches the model, and the session keeps tool calls.
  - DI-based lifecycle, and architecture tests.
  - A stable Anthropic integration (Python's is still prerelease).
- **Python's strengths:**
  - Less ceremony, and contract validation straight from the schema files.
  - The SDK's own per-attempt middleware.
- **Python's costs:** more framework edges to handle yourself: tool-call pairing in history, closing a supplied client, cancellation semantics, threads for sync tools.
- **For a team, the deciding factors** are usually the team's language, hosting (Aspire works for both), and how much prerelease integration risk it accepts.
