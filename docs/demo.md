# Demonstration

A short tour of what the system does, run against **both** backends in fake model mode (no key, no cost).

> **Assistant-captured.** The output below is real, captured on 2026-11-12 by the assistant (Claude Code) by running [`scripts/demo.sh`](../scripts/demo.sh) against each backend started by the AppHost. Most of it comes from a fresh clone at commit `ae66f38`. The write section was recaptured after the script switched to a repeatable write: it was run twice in a row, and the second run is shown.

## Run it yourself

```bash
git clone https://github.com/olaheavy-dev/maf-support-lab && cd maf-support-lab
dotnet run --project apphost                      # starts both backends (fake mode) and the Aspire dashboard
scripts/demo.sh http://localhost:5180             # the .NET backend
scripts/demo.sh https://localhost:<port> -k       # the Python backend: Aspire serves it over https on a port shown in the dashboard
```

The demo needs `curl`, `jq` and `uuidgen`. It can be run again and again: each run opens a new support ticket. If no approval is proposed, which happens only outside fake mode, it stops with a message. Prerequisites and troubleshooting are in the [runbook](runbook.md).

## What it shows

Each step is one HTTP request; the lines are the fields that matter, trimmed by `jq`. Both backends produced the same results. The only differences are id lengths and timestamp precision, which the [contract](../contracts/README.md) leaves to each implementation.

### An answer

> Where is my order ORD-1002?

```json
{"outcome":"completed","route":"orders","reply":"Order ORD-1002 is shipped."}
```

Triage classified the message, code routed it to the orders agent, and the agent answered with its `get_order_status` tool over the synthetic fixtures.

### A denial that reveals nothing

> What's the status of ORD-2001?

```json
{"outcome":"completed","reply_kind":"not_found","reply":"I couldn't find order ORD-2001 on your account. Please check the order id."}
```

ORD-2001 exists, but it belongs to another customer, so it answers exactly like a missing order ([threat model](threat-model.md#core-rules)).

### Not eligible, so the customer isn't asked to approve

> Cancel order ORD-1001.

```json
{"outcome":"completed","reply_kind":"not_eligible","operations":0}
```

ORD-1001 was delivered and can't be cancelled. Eligibility is checked *before* any approval is proposed, so no operation was created.

### A write waits for approval, and runs once

> Please open a support ticket: my smart fridge firmware shows error Z-77.

```json
{"outcome":"awaiting_approval","pending":{"kind":"create_ticket","arguments":{"description":"Please open a support ticket: my smart fridge firmware shows error Z-77.","priority":"normal","subject":"Support request"},"expires_at":"2026-11-12T17:13:46Z"}}
```

Approved twice (the second run of the demo, so the ticket is the store's second):

```json
{"outcome":"completed","turn_id":"turn_df1521d9032afeac9886","reply":"Done: support ticket TKT-0002 is open."}
{"outcome":"completed","turn_id":"turn_df1521d9032afeac9886","reply":"Done: support ticket TKT-0002 is open."}
{"status":"committed","result":{"ticket_id":"TKT-0002"}}
```

The second approval returns the same resumed turn, and the operation record is the authority: one ticket. Tickets are numbered from a stored counter, which is also how the load scenarios count writes under 16 concurrent approvals ([load results](evaluations/load.md)).

### Changing your mind afterwards is refused

```json
{"status":409,"code":"approval_already_decided"}
```

### A model failure is a result, not a crash

> Where is ORD-1002? [fake:model-error]

```json
{"outcome":"failed","error":"internal_error"}
```

The fake model fails on purpose, and the turn ends `failed` with a contract error code. The conversation stays usable.

### Identity can't be smuggled in

A turn request with an extra `customer_id` field:

```json
{"status":400,"code":"invalid_request"}
```

The principal comes only from the host. No request schema accepts identity.

## Implemented, simulated, and not done

| | |
| --- | --- |
| **Implemented and verified** | Triage and code routing; three specialist agents with scoped tools; approval-bound writes, run at most once; durable state across restarts; streaming and cancellation; access, injection, fault and concurrency tests. Both backends agree on 29 contract probes and every evaluation dataset ([testing](testing.md), [evaluations](evaluations/README.md)). |
| **Synthetic or simulated** | Customers, orders, invoices, payments and the knowledge base are fixtures. Cancellations, refunds and tickets change only local simulated state. Identity is one fixed development customer per process. |
| **SDK differences** | None visible to a client. Internal differences, such as how each SDK carries a paused approval, are in [0004](decisions/0004-sdk-comparison.md). |
| **Not done** | Real authentication, rate limiting, retention, hosting hardening and real side effects: see the [production requirements](threat-model.md#production-requirements-explicit-follow-ups). |

With a model key, the same flow runs against Claude (`claude-haiku-5-5`); see [live mode](runbook.md#live-mode-opt-in-billed). Model-judged reply quality from live runs is in [quality](evaluations/quality.md).
