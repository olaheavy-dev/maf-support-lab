# Support API contract — version 1

This folder is the single specification both backends implement: the .NET API (L03+) and the Python API (L17+). Their internals use each SDK idiomatically; their observable HTTP and stream behavior must match this contract.

- `schemas/`: JSON Schema (draft 2020-12) for every request, response, stream event, and error, plus two internal shapes (triage decision, routed request).
- `examples/valid/`: examples that must pass. `examples/invalid/`: examples that must be rejected. `examples/index.json` maps each example to its schema.
- `tests/`: offline checks run in CI (see [Running the checks](#running-the-checks)).

Current revision: **1.0** (see [Revision history](#revision-history)).

## Conventions

| Topic | Rule |
| --- | --- |
| Base path | `/api/v1`. The path carries the major version. |
| Body format | JSON, `snake_case` property names. .NET configures a snake_case naming policy; Python uses snake_case fields. |
| `contract_version` | Present in every response body and stream event as `"1.<minor>"`. Requests don't carry it. |
| Timestamps | RFC 3339 UTC with `Z`, e.g. `2026-11-01T12:00:03Z`. |
| Identifiers | Server ids are prefixed opaque strings: `conv_…`, `turn_…`, `op_…`, `appr_…`. Clients must not parse them. `client_turn_id` is a client-generated UUID. Fixture ids: `CUST-NNNN`, `ORD-NNNN`, `INV-NNNN`, `KB-NNN`. |
| Money | `{ "amount_minor": 8639, "currency": "USD" }`: integer cents, never floats. |
| Unknown fields | Requests with unknown properties are rejected (`invalid_request`). Responses contain only documented properties within a revision. |
| Trace reference | `trace_id` is a W3C trace id for diagnosis. It is not a secret and never contains payload data. |

## Identity and ownership

No request carries identity. There is no `customer_id`, user header, or confirmation flag in any request schema, and any such property is rejected. The server-side principal comes from the host: a fixed development principal (L03), or a trusted test-host injection point in tests. Every read and write checks ownership against that principal inside application services, regardless of what the model or client sends.

A conversation, turn, operation, or approval owned by someone else returns **404 `not_found`**, exactly like one that doesn't exist. This avoids revealing what exists. Inside a turn, an order owned by another customer gets the same `not_found` reply as an unknown order.

## Routes

| Method and path | Request schema | Success | Notes |
| --- | --- | --- | --- |
| `POST /api/v1/conversations` | `create-conversation-request` (empty object) | 201 `conversation` | Owner is the principal; `backend` is the server's alias (`dotnet` or `python`). |
| `GET /api/v1/conversations/{conversation_id}` | — | 200 `conversation` | Used after a refresh: shows `active_turn_id` and `pending_approvals`. |
| `POST /api/v1/conversations/{conversation_id}/turns` | `turn-request` | 200 `turn-result`, or an SSE stream | See [Turns](#turns) and [Streaming](#streaming). |
| `GET /api/v1/conversations/{conversation_id}/turns/{turn_id}` | — | 200 `turn-result` | `state` is `running` or `finished`. Authoritative after a dropped stream. |
| `POST /api/v1/conversations/{conversation_id}/turns/{turn_id}/cancel` | empty | 202 `cancel-turn-response` | Cooperative; see [Cancellation](#cancellation). |
| `POST /api/v1/conversations/{conversation_id}/approvals/{approval_id}` | `approval-decision-request` | 200 `turn-result`, or an SSE stream | Resumes the paused work as a new turn with `resumed_from_turn_id`. |
| `GET /api/v1/operations/{operation_id}` | — | 200 `operation` | Authoritative business state of a write. |

Health endpoints (`/health`, `/alive`) belong to the host (L03), not to this contract.

## Turns

A turn is one customer message and everything the system does in response.

- **One final outcome.** A finished turn has exactly one `outcome`:
  - `completed`: the turn ended normally. `reply.kind` says how: `answer`, `clarification`, `not_found`, `not_eligible`, `escalation_offer`, `approval_result`, or `refusal`.
  - `awaiting_approval`: the turn paused because a write needs the customer's decision. **This is a pause, not success.** Nothing has been changed yet.
  - `failed`: the turn could not finish. `error.code` is one of `model_unavailable`, `tool_unavailable`, `invalid_model_output`, `rate_limited`, `timeout`, `budget_exhausted`, `internal_error`.
  - `cancelled`: the turn stopped on request before doing anything further.
- **Routing.** `route.status` is `routed` (with a `target`) or `unrouted` (with `unrouted_reason`). When triage output is invalid or ambiguous the turn still completes with a `clarification` reply; it never guesses a route.
- **Idempotent submission.** `client_turn_id` is required. Re-sending the same `client_turn_id` with the same message returns the original turn (its current state) and never runs it again. Re-using it with a different message returns `409 client_turn_id_reused`.
- **One turn at a time per conversation.** While a turn is running, a new turn in the same conversation gets `409 turn_in_progress`. Other conversations are unaffected.
- **Eligibility before approval.** Services check eligibility *before* asking for approval, so customers are not asked to approve impossible actions (`reply.kind = not_eligible`, no operation created). They check again, authoritatively, when the approved operation executes.

## Approvals and operations

Every write (`cancel_order`, `refund_order`, `create_ticket`) requires approval in this baseline.

1. The turn ends `awaiting_approval` with `pending_approval`: `approval_id`, `operation_id`, `kind`, the **exact** `arguments`, a readable `summary`, and `expires_at` (15 minutes).
2. The server binds the approval to the principal, conversation, operation, kind, and exact arguments. The decision request carries only `operation_id`, `decision` (`approve` or `reject`), and an optional `reason`. It cannot change arguments.
3. Approve: the operation runs and the resumed turn reports the factual result. Reject: nothing changes and the operation becomes `rejected`. A rejection must not lead the model to immediately ask again.

Operation `status` is the authoritative record, independent of any connection:

| Status | Meaning |
| --- | --- |
| `awaiting_approval` | Proposed, waiting for a decision until `expires_at`. |
| `rejected` | The customer rejected it. Nothing changed. |
| `expired` | No decision in time. Nothing changed. |
| `running` | Approved and executing. |
| `committed` | The change is recorded; `result` holds the facts (refund id and amount, cancelled order, ticket id). |
| `failed` | Approved but not executed; `failure.code` is `not_eligible`, `not_found`, `timeout`, `cancelled` (1.1), or `internal_error`. Nothing changed. |

Decision errors: wrong `operation_id` → `409 approval_mismatch`; a different decision after one is recorded → `409 approval_already_decided`; too late → `410 approval_expired`. **Repeating the same decision is idempotent:** it returns the current result and never runs the write twice.

## Streaming

Send `Accept: text/event-stream` to the turn or approval route to receive Server-Sent Events. Otherwise the response is a single JSON `turn-result`. Browsers read the stream with `fetch` (the route is POST, so `EventSource` doesn't apply).

```
id: 5
event: reply.delta
data: {"contract_version":"1.0","type":"reply.delta","conversation_id":"conv_…","turn_id":"turn_…","sequence":5,"data":{"text":"Order ORD-1002 shipped "}}
```

- Each event's `data` line is one `stream-event` JSON document. The SSE `event` equals `type`, and the SSE `id` equals `sequence`, which starts at 1 and increases by 1.
- **Event types:**
  - `turn.started`;
  - `turn.progress`: stage, route and tool name; never message content;
  - `reply.delta`: text, which may be the model's tokens as they arrive;
  - `reply.reset` (1.1): discard the reply text shown so far;
  - **`turn.final`**: the one terminal event, carrying the finished `turn-result`.
- **Reply text:** the `reply.delta` texts sent after the last `reply.reset` (or from the start, if there was none) concatenate to the final `reply.text`. If the final result has no reply, a stream that sent text ends with `reply.reset` before `turn.final`.
  - The server sends `reply.reset` when the text it streamed is not the reply it settles on. For example, the model starts "Sure, I will refund…" but the turn pauses for approval with a server-written request.
  - Partial text is display-only and never drives routing, approval or any decision. `turn.final` is authoritative.
- `turn.final` is sent only while the connection still works. **A client that disconnects may never receive it.** Recover with `GET …/turns/{turn_id}` and `GET /api/v1/operations/{operation_id}`. Version 1 does not replay deltas (`Last-Event-ID` is ignored).
- A comment line `: keepalive` may be sent every 15 seconds.
- Errors detected before the stream starts are normal HTTP problem responses. After `turn.started`, failures end with `turn.final` and outcome `failed`.

## Cancellation

`POST …/turns/{turn_id}/cancel` asks the turn to stop and returns 202. Cancellation is cooperative:

- Work checks for cancellation at a defined pre-commit boundary and starts nothing new once it sees it.
- Cancelled before a write commits: outcome `cancelled`, no change. An approved operation that was stopped this way becomes `failed` with `failure.code` `cancelled` (1.1).
- A write already committed stays committed. The turn and operation report the factual committed state; nothing is rolled back.
- A dropped connection is **not** a cancellation request and does not prevent or undo a write. Cancelling a finished turn returns `409 turn_already_finished`.

## Errors

HTTP-level failures use RFC 9457 problem details (`application/problem+json`, `problem` schema). A turn that runs and then fails is **not** an HTTP error: it returns 200 with outcome `failed`.

| `code` | HTTP | When |
| --- | --- | --- |
| `invalid_request` | 400 | Body or parameters don't match the contract; `errors[]` lists JSON pointers. |
| `not_found` | 404 | Missing **or not owned by you**. |
| `turn_in_progress` | 409 | Another turn is running in this conversation. |
| `turn_already_finished` | 409 | Cancel requested for a finished turn. |
| `client_turn_id_reused` | 409 | Same `client_turn_id`, different message. |
| `approval_already_decided` | 409 | A different decision was already recorded. |
| `approval_mismatch` | 409 | `operation_id` doesn't match the approval. |
| `approval_expired` | 410 | The approval window passed; nothing changed. |
| `rate_limited` | 429 | Includes `retry_after_seconds`. |
| `service_unavailable` | 503 | The backend can't accept work right now. |
| `internal_error` | 500 | Unexpected failure; use `trace_id` to diagnose. |

Error text is safe to show to users: no secrets, stack traces, or raw provider payloads.

## Internal shapes

- `triage-decision`: the structured output the triage model must produce. Both backends validate model output against this **full** schema before routing, and must not loosen it to fit a model (L07/L18). Some providers' structured-output modes accept only a subset of JSON Schema (for example, numeric ranges). The model-facing schema may then be simplified, but the application still validates against this one. Valid shape is not proof of correctness or authorization; `confidence` is uncalibrated and never used for decisions.
- `routed-request`: what a specialist receives. It keeps the customer's `original_message` beside the validated triage fields. The principal is never in this payload; it travels in trusted server-side context.

## Revision policy

Version 1 is the initial shared specification. The approval and streaming parts are **provisional** until checked against the native SDKs: **L10** (approval) and **L12** (streaming) are the planned refinement checkpoints.

- **Minor revision (1.x):** backward-compatible additions only, such as new optional response properties or new event types clients may ignore.
- **Major revision (2.0, `/api/v2`):** any breaking change, such as removing or renaming fields, changing meanings, tightening request rules existing clients follow, or changing status codes.
- **Every revision, in the same PR:**
  1. Update the schemas.
  2. Update the examples (valid and invalid).
  3. Add an entry to the revision history below with the rationale and compatibility impact.
  4. Update every already implemented adapter and its tests.
- Later tickets implement the current agreed revision.
- **Never weakened to fit an SDK:** ownership checks, exact-argument approval binding, and factual operation state. If an SDK can't support them directly, the adapter does extra work; the contract doesn't bend.

## Revision history

| Version | Ticket | Change | Compatibility |
| --- | --- | --- | --- |
| 1.0 | L02 | Initial specification. Approval and streaming provisional until L10/L12. | — |
| 1.0 (checked) | L10 | Approval checked against the native .NET SDK (MAF 1.28.0 / M.E.AI 10.11: `ApprovalRequiredAIFunction`, `ToolApprovalRequestContent`, `CreateResponse(approved, reason)`). **No change needed.** The native request carries the exact tool call, which maps to `kind` and `arguments`, and the decision's optional `reason` maps to the native response reason. Native approval has no customer, conversation, operation, or expiry binding and no operation state; the server adds those, exactly as specified here. | None |

## Running the checks

```bash
python3 -m venv .venv
.venv/bin/pip install --require-hashes -r contracts/requirements.txt
.venv/bin/python -m unittest discover -s contracts/tests
.venv/bin/python -m unittest discover -s fixtures/tests
```

`requirements.txt` is generated from `requirements.in` with hashes (command at the top of `requirements.in`). These are test tools only, not application dependencies.
| 1.1 | L12 | **Streaming checked against the native .NET SDK** (MAF 1.28.0: `AIAgent.RunStreamingAsync` → `AgentResponseUpdate`; workflows `InProcessExecution.RunStreamingAsync` + `WatchStreamAsync`). **Added the `reply.reset` event and the operation failure code `cancelled`.** Live model tokens show the model's words before the server knows the final reply. A native stream delivers text *before* a `ToolApprovalRequestContent`, and the server may then replace it (approval request, declined write), so the client needs a way to discard it. The text rule now counts only deltas after the last reset. An approved write stopped by cancellation before it commits needed a truthful status. | Additive (new event type, new enum value). A 1.0 client that ignores `reply.reset` may show stale partial text until `turn.final`, whose `reply.text` stays authoritative; streams without a reset are unchanged. Readers of operation `failure.code` must accept `cancelled`. |
