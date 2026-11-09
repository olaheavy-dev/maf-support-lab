# Learning-first collaboration

The repository exists to teach its owner Microsoft Agent Framework, engineering judgment, and interview communication.

## Collaboration mode

The assistant drives each ticket end to end; the owner reviews and decides.

- Implement the active ticket completely: code, tests, docs, the evidence note, a branch, and a pull request.
- Explain in plain, simple English. Put the review brief (what changed, what to check) on the PR, knowledge the owner should learn on the ticket, and a short summary plus anything needing the owner in the chat.
- Keep the project board current: move the active ticket to In progress when work starts and to Review when its PR opens. After the PR merges, move it to Done and the next ticket in Order to Ready.
- Merge a PR (squash) once required checks pass and review threads are resolved. Do not start implementing the next ticket without the owner's go-ahead, and never mark a ticket learned; the owner decides that.
- Label assistant-written evidence and interview answers as assistant-drafted. Leave a section for the owner's own explanation.
- Never invent learning evidence, interview answers, test results, or elapsed study time.
- Use the latest stable release of every runtime and package (see docs/version-policy.md), and the cheapest model adequate for live checks; escalate only on recorded evidence.

## Engineering

- Read the active issue and its dependencies, docs/version-policy.md, and docs/sources.json.
- Verify API signatures against the selected released version; surface documentation disagreements.
- Keep C# and Python idiomatic while preserving the shared observable behavior.
- Changes need meaningful verification appropriate to their effect; avoid tests that merely mirror implementation.
- Keep business authorization, approval enforcement, and idempotency in application/tool code.
- Use synthetic fixtures. Exclude credentials, local sessions, checkpoints, and sensitive telemetry from Git.
- Do not add infrastructure or abstractions without a ticket-supported need.

## Scope

The initial roadmap is a plan. Empty application directories are intentional. Do not generate the full application while setting up governance or planning.
