# Learning workflow

## Work one ticket at a time

Follow the roadmap's execution Order column and GitHub blocking relationships. Stable LNN ticket IDs and issue numbers survive reordering; they are not execution positions. Later work remains Backlog; the first unblocked ticket is Ready.

Build both support APIs and the shared frontend through L24 before starting E08 formal evaluations or E09 Gatsby documentation. Feature tests and learner notes/decisions remain part of each implementation ticket. The formal sequence is projects -> evaluation datasets/runner -> Microsoft native evaluation -> budgeted quality scoring -> reporting -> Markdown/Gatsby static artifacts -> final operational/interview work.

For each ticket:

1. Read the source files and current documentation for that task.
2. Before coding, explain the request lifecycle and the smallest intended change.
3. The assistant implements the ticket and opens a PR with a brief: key facts, what to note, and what to review.
4. Verify observable success and meaningful failure paths.
5. Write a short evidence note and answer the interview questions aloud.
6. Review and merge the PR, then select the next ticket.

## Ticket structure

Every learning ticket contains: parent epic; order and prerequisites; concrete implementation steps and files; course source links; Microsoft Learn links; release/version guidance; measurable acceptance criteria; learning outcomes; interview questions; an independent stretch exercise; and closing evidence.

Use the [alignment contract](alignment.md) and [capability map](capabilities.md) to check what already exists before a ticket starts. Explicit scope boundaries prevent a task from depending on later work.

The stretch exercise is optional and is not a closing requirement. If it needs a future capability, defer it. Python parity is already split into focused tickets: L19 sessions -> L37 approvals -> L38 durability -> L39 access checks -> L20 streaming -> L40 telemetry -> L41 injection boundaries -> L42 resilience. Each has its own acceptance criteria, evidence note, and blocking link.

Backlog JSON and the source registry are canonical. After changing either, run:

    python3 scripts/render_learning.py
    python3 scripts/validate_repository.py

Readable tickets, epic pages, the roadmap, and the capability map are generated. A source update must also be synchronized to the matching GitHub issue while preserving completed checkboxes, evidence, comments, and status. Rendering files does not update GitHub automatically.

Course references are comparison material when the planned behavior is absent from the course. Official docs are live; record the date and recheck against release notes and tagged SDK source before implementation.

## Contract revision checkpoints

L02 establishes the initial versioned contract. L10 checks native approvals and L12 checks native streaming against it before Python starts. A justified revision updates schemas, examples, contracts/README.md history, and every already implemented adapter/test in one PR, with rationale and compatibility/version impact. Later tickets use the current agreed revision. L22 is a browser compatibility check, not the first opportunity to correct a contract.

## Size and scope

Aim for one to three focused sessions per ticket. If a ticket exceeds that, split it before adding more implementation. A failed experiment can be useful evidence when its limitation and next step are clear.

## Interview practice

Use: problem -> design -> execution flow -> tradeoff -> verification -> limitation. Explain without reading generated answers. Demonstrate one fault and use telemetry to diagnose it.

## Completion

Code passes the acceptance criteria, the evidence is honest, and the learner can explain the change. Record unanswered questions rather than letting a tool claim understanding.

The assistant can review the evidence and ask follow-up questions. The learner decides whether they understand the material.
