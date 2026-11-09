# Learning workflow

## Work one ticket at a time

Use the numeric L01-L28 sequence as the default path. Dependencies are recorded in the roadmap and GitHub issues. Later work remains Backlog; the first unblocked ticket is Ready.

For each ticket:

1. Read the source files and current documentation for that task.
2. Before coding, explain the request lifecycle and the smallest intended change.
3. Attempt the implementation yourself. Ask the assistant for hints, a design review, or debugging help.
4. Verify observable success and meaningful failure paths.
5. Write a short evidence note and answer the interview questions aloud.
6. Review and merge the PR, then select the next ticket.

## Ticket structure

Every learning ticket contains: parent epic; order and prerequisites; concrete implementation steps and files; course source links; Microsoft Learn links; release/version guidance; measurable acceptance criteria; learning outcomes; interview questions; an independent stretch exercise; and closing evidence.

Use the [alignment contract](alignment.md) and [capability map](capabilities.md) to check what already exists before a ticket starts. Explicit scope boundaries prevent a task from depending on later work.

The stretch exercise is optional and is not a closing requirement. If it needs a future capability, defer it. L19/L20 are porting slices across several previously learned concepts; split their listed steps into linked sub-tasks before implementing if needed.

Backlog JSON and the source registry are canonical. After changing either, run:

    python3 scripts/render_learning.py
    python3 scripts/validate_repository.py

Readable tickets, epic pages, the roadmap, and the capability map are generated. A source update must also be synchronized to the matching GitHub issue while preserving completed checkboxes, evidence, comments, and status. Rendering files does not update GitHub automatically.

Course references are comparison material when the planned behavior is absent from the course. Official docs are live; record the date and recheck against release notes and tagged SDK source before implementation.

## Size and scope

Aim for one to three focused sessions per ticket. If a ticket exceeds that, split it before adding more implementation. A failed experiment can be useful evidence when its limitation and next step are clear.

## Interview practice

Use: problem -> design -> execution flow -> tradeoff -> verification -> limitation. Explain without reading generated answers. Demonstrate one fault and use telemetry to diagnose it.

## Completion

Code passes the acceptance criteria, the evidence is honest, and the learner can explain the change. Record unanswered questions rather than letting a tool claim understanding.

The assistant can review the evidence and ask follow-up questions. The learner decides whether they understand the material.
