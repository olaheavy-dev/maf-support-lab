# Repository governance

Target: main.

- Require pull requests.
- Require the repository-checks status check after its first successful run.
- Require conversations to be resolved.
- Require linear history; use squash merge.
- Block force pushes and deletion of main.
- Delete merged feature branches automatically.
- Use read-only default workflow permissions; do not let Actions approve pull requests.
- Enable dependency alerts and supported secret protections.
- Keep a CODEOWNERS file for visibility.

This is a solo repository: required approval count is zero. The owner reviews every diff, and an automated Codex review runs on each pull request. When another reviewer joins, raise the approval requirement to one and enable stale-approval dismissal. Requiring the author to obtain their own approval would block normal progress.

The required `repository-checks` status is an aggregate job: it waits for every job (`contracts-and-docs`, `dotnet`, `python`, `cross-backend`, `evaluations`) and passes only if all succeeded. Never require a check that cannot run on every relevant PR. See [testing](testing.md) for the offline/live split and the separate dependency audit.

The only application secret is an optional `ANTHROPIC_API_KEY` for the manual, budgeted live-quality workflow. Live-model evaluation is opt-in and bounded.

GitHub settings are verified separately from files in this repository; committing this document does not activate protections. Secret scanning and push protection are enabled; code scanning runs CodeQL.

References:
- https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets
- https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets
