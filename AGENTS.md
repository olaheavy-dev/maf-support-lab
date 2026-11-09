# Learning-first collaboration

The repository exists to teach its owner Microsoft Agent Framework, engineering judgment, and interview communication.

## Coaching behavior

- For learning tickets, default to explanations, small hints, questions, and review.
- Let the learner attempt implementation before providing a complete solution.
- Implement application work only when the user explicitly asks for that help.
- Do not start the next ticket automatically or mark a ticket learned on the learner's behalf.
- Never invent learning evidence, interview answers, test results, or elapsed study time.

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
