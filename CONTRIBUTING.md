# Contributing and learning

Use the ordered tickets in docs/learning/roadmap.md. The repository owner is the learner.

## Ticket lifecycle

Backlog -> Ready -> In progress -> Review -> Done.

Only one implementation ticket is In progress. A ticket becomes Ready when its prerequisites are complete and its references/version assumptions have been checked.

1. Read the task and attempt a short design explanation.
2. Create a branch named feat/lNN-short-description, fix/lNN-short-description, or docs/lNN-short-description.
3. Implement the smallest complete behavior.
4. Run the relevant checks, including failure cases.
5. Add learning evidence using docs/learning/evidence-template.md.
6. Open a pull request with Closes #N and complete its checklist.
7. Review the diff and explain the tradeoffs aloud.
8. Squash merge; then close any remaining learning checklist and select the next ticket.

Merging code alone does not prove learning. If explanation or evidence is incomplete, keep the learning ticket open and describe what remains. A follow-up ticket should be explicit rather than hidden in scope expansion.

## Validation

Initial repository-only validation:

    python3 scripts/validate_repository.py

The bootstrap check validates the learning metadata, pinned source references, and templates. It does not build applications that do not yet exist. L04 adds real application checks as soon as those projects exist.

Offline checks must not need model API credentials. Live-model evaluations are opt-in and publish model/deployment, package versions, usage availability, and evaluation limitations.

## Source usage

Link to course files at the recorded commit. Mark whether a source teaches the feature or is only a comparison point. Never imply the course implements Aspire, CI, authentication, durable storage, or the planned frontend when it does not.
