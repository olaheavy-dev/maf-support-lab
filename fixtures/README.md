# Synthetic fixtures

Shared, entirely synthetic data and feature-test scenarios used by both backends. No real people, payment data, or production records. Names use `example.com`; card endings are well-known test values.

| File | Contents |
| --- | --- |
| `data/customers.json` | Two customers: `CUST-1001` (Ada Example) and `CUST-1002` (Ben Sample). |
| `data/orders.json` | Six orders with statuses, dates, items, and totals in cents. |
| `data/invoices.json`, `data/payments.json` | Billing records for every order, including one recorded refund. |
| `data/knowledge-base.json` | Three troubleshooting articles (`KB-101`…`KB-103`). |
| `data/policies.json` | Eligibility rules, the fixture clock, and approval rules, as data. |
| `scenarios/feature-scenarios.json` | 19 feature-test scenarios (S01–S19) with expected observable behavior. |
| `schemas/fixtures.schema.json` | Schema for all of the above. |

## Eligibility rules (`policies.json`)

- **Fixture clock:** `reference_time` = `2026-11-01T12:00:00Z`. Eligibility is computed against this fixed time, never the wall clock, so results never drift.
- **Cancel:** only orders with status `processing`.
- **Refund:** only `delivered` orders, at most 30 days after `delivered_at`, not already refunded. The amount is the full order total.
- **Approval:** required for `cancel_order`, `refund_order`, and `create_ticket`; it expires after 15 minutes.

Resulting outcomes:

| Order | Owner | Status | Cancel? | Refund? |
| --- | --- | --- | --- | --- |
| ORD-1001 | CUST-1001 | delivered 12 days ago | no | **yes** |
| ORD-1002 | CUST-1001 | shipped | no | no (not delivered) |
| ORD-1003 | CUST-1001 | processing | **yes** | no |
| ORD-2001 | CUST-1002 | delivered 52 days ago | no | no (outside window) |
| ORD-2002 | CUST-1002 | processing | **yes** | no |
| ORD-2003 | CUST-1002 | delivered, already refunded | no | no (refunded) |

## Scenarios

Each scenario names the acting `principal` (set through the trusted test host, never through a request), ordered `steps` (`send` a message, `decide` an approval, optionally `as_principal` another customer, or `resend_previous`), per-step expectations, and the expected business state change.

Expectations assert observable facts: route, outcome, reply kind, operation status, text that must or must not appear, and HTTP error code. They don't assert exact wording. These are feature-test inputs, not the formal evaluation datasets introduced.

## Reset and isolation rules

1. **Fixture files are read-only.** Backends load them at startup into their own store; nothing writes back to these files.
2. **One namespace per backend and run.** Mutable state (orders after cancellation, refunds, tickets, operations, approvals, sessions) lives under `{backend}/{run_id}`, e.g. `dotnet/test-7f3a`. Resetting one namespace never affects another backend or run.
3. **Each test starts clean.** A feature test creates a fresh namespace (or resets its own) before it runs, so tests can run in any order and in parallel.
4. **Stable ids.** Fixture ids never change meaning. New records get new ids. Ids created at runtime (operations, tickets, refunds) are unique within a namespace.
5. **No shared mutable state between backends.** The .NET and Python backends share these definitions, never a live store.
6. **The clock is injected.** Fake mode and tests use `reference_time`. Live checks use it for eligibility too, so their expectations match.

## Checks

`fixtures/tests/test_fixtures.py` verifies:
- every file matches its schema;
- ids are unique and records reference existing owners and orders;
- invoice totals add up;
- the two customers have distinct eligibility outcomes;
- every scenario's expectation agrees with the policy and with ownership.

Run instructions are in [contracts/README.md](../contracts/README.md#running-the-checks).
