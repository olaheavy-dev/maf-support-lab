# Working in this repository

Guidance for anyone, person or coding agent, changing this repository. It holds one customer-support system built twice with Microsoft Agent Framework (C# and Python) against one contract; see the [README](README.md) and the [documentation index](docs/README.md).

## How changes land

1. Work on a short-lived branch; one change per pull request.
2. Before coding a non-trivial change, state what will be built and why.
3. Implement it completely: code, tests, and the documentation it affects.
4. Open a pull request that says what changed, how it was verified, and what could break. Request an automated review by commenting `@codex review`.
5. Fix valid review findings, and resolve every thread. Squash-merge only when the required `repository-checks` status passes and the review is clean.
6. Decisions that belong to the owner (scope, repository settings, anything billed or hard to reverse) are asked as explicit questions, one per decision, with the recommended option first.

Never commit credentials, local stores, checkpoints, captured prompts or sensitive telemetry. Never invent test results; label output an assistant produced as assistant-captured.

## Engineering rules

- **Versions:** use the latest stable release of every runtime and package ([version policy](docs/version-policy.md), [0001](docs/decisions/0001-version-baseline.md)). Verify API signatures against the selected release, and record any disagreement with the documentation.
- **Behaviour parity:** keep C# and Python idiomatic while preserving the shared observable behaviour. A contract change updates the schemas, both backends, the probes, and the evaluation datasets together.
- **Authority in code:** business authorization, approval enforcement and idempotency live in application and tool code, never in prompts or model output.
- **Data:** use the synthetic fixtures only.
- **Verification:** a change needs checks that would fail if it were wrong (mutation-test the important ones); avoid tests that mirror the implementation. Offline checks never need a model key.
- **Live model use:** opt-in, bounded and recorded. Use the cheapest adequate model (`claude-haiku-5-5`), and escalate only on recorded evidence.
- **Scope:** add no infrastructure or abstraction without a concrete need in the change at hand.
