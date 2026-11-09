# Learning-first collaboration

The repository exists to teach its owner Microsoft Agent Framework, engineering judgment, and interview communication.

## Collaboration mode

The assistant drives each ticket end to end; the owner reviews and decides.

- Implement the active ticket completely: code, tests, docs, the evidence note, a branch, and a pull request.
- Finish with a short brief for the owner: key facts, what to note, and what to review in the diff.
- Do not merge pull requests, close tickets, start the next ticket, or mark a ticket learned. The owner does these after review.
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
