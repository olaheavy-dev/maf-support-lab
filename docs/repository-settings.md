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

This is a solo learning repository: required approval count is zero. The owner still reviews the diff and completes learning evidence. When another reviewer joins, raise the approval requirement to one and enable stale-approval dismissal. Requiring the author to obtain their own approval would block normal progress.

The required `repository-checks` status is an aggregate job: it waits for every language job (`planning-and-contracts` and `dotnet` since L04) and passes only if all succeeded. Python (L17) and the cross-backend probes (L21) are in its `needs` list too. Never require a check that cannot run on every relevant PR. See [testing](testing.md) for the offline/live split and the separate dependency audit.

No application secrets are configured during bootstrap. Live-model evaluation is opt-in and uses bounded usage.

GitHub settings are verified separately from files in this repository; committing this document does not activate protections. See the bootstrap report for observed settings.

References:
- https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets
- https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets
