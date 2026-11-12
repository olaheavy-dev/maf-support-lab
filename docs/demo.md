# Demonstration

A short tour of what the system does, run against **both** backends from a fresh clone, in fake model mode (no key, no cost). The output below is real, captured on 2026-11-12 from commit `ae66f38`: [`scripts/demo.sh`](../scripts/demo.sh) run against each backend started by the AppHost.

## Run it yourself

```bash
git clone https://github.com/olaheavy-dev/maf-support-lab && cd maf-support-lab
dotnet run --project apphost                      # starts both backends (fake mode) and the Aspire dashboard
scripts/demo.sh http://localhost:5180             # the .NET backend
scripts/demo.sh https://localhost:<port> -k       # the Python backend: Aspire serves it over https on a port shown in the dashboard
```

The demo needs `curl`, `jq` and `uuidgen`. Prerequisites and troubleshooting are in the [runbook](runbook.md).

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

> Please cancel order ORD-1003.

```json
{"outcome":"awaiting_approval","pending":{"kind":"cancel_order","arguments":{"order_id":"ORD-1003"},"expires_at":"2026-11-12T17:06:50Z"}}
```

Approved twice:

```json
{"outcome":"completed","turn_id":"turn_58291c3459ac1f2d88e7","reply":"Done: order ORD-1003 is cancelled."}
{"outcome":"completed","turn_id":"turn_58291c3459ac1f2d88e7","reply":"Done: order ORD-1003 is cancelled."}
{"status":"committed","result":{"order_id":"ORD-1003","order_status":"cancelled"}}
```

The second approval returns the same resumed turn. The operation record is the authority: it committed once. Under 16 concurrent approvals a probe ticket confirms one write ([load results](evaluations/load.md)).

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
