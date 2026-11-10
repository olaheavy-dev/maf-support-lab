# Learning-first collaboration

The repository exists to teach its owner Microsoft Agent Framework, engineering judgment, and interview communication.

## Collaboration mode

The assistant drives each ticket end to end; the owner reviews and decides.

- Follow the same steps for every ticket:
  1. Read the ticket, its prerequisites, and its references.
  2. Before coding, tell the owner in chat: a short summary, then a detailed plain-English explanation of what will be built and why.
  3. Implement it completely on a branch: code, tests, docs, and the evidence note. **Do not commit yet.**
  4. Stop and give the owner a local-review brief: what changed file by file, what the code does, what to understand, and how to run the checks. Wait for the owner's go-ahead.
  5. After the go-ahead: commit, push, open the PR, post the review brief and learning note, and request a Codex review by commenting `@codex review`.
  6. Wait for the Codex review to finish. Explain each finding in plain English, and fix valid ones (changes go back through the owner's go-ahead). Merge only when the checks pass and Codex has no unresolved findings, then report with the PR and comment links.
- Explain in plain, simple English. Put the review brief (what changed, what to check) on the PR, knowledge the owner should learn on the ticket, and a short summary plus anything needing the owner in the chat.
- Keep the project board current: move the active ticket to In progress when work starts and to Review when its PR opens. After the PR merges, move it to Done and the next ticket in Order to Ready.
- Never commit before the owner's go-ahead. After it, merge the PR (squash) once required checks pass, Codex has reviewed it, and review threads are resolved. Do not start implementing the next ticket without the owner's go-ahead, and never mark a ticket learned; the owner decides that.
- Label assistant-written evidence and interview answers as assistant-drafted.
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
