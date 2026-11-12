# Testing and verification

Two kinds of verification are kept apart:

- **Offline checks:** deterministic, need no credentials, cost nothing, and run on every pull request. They decide whether a change can merge.
- **Live checks:** call a real model. They are opt-in, bounded, never required for merging, and reported separately with their model, versions, and usage.

## Offline (required)

Every pull request and every push to `main` runs `.github/workflows/repository-checks.yml`:

| Job | What it checks |
| --- | --- |
| `contracts-and-docs` | Contract schemas and examples, fixtures and scenarios, and the documentation links: every relative link, anchor and image in the tracked Markdown, and every `docs/` page reachable from [docs/README.md](README.md) (plus the link checker's own tests). |
| `dotnet` | Locked restore, Release build with warnings as errors, and the .NET integration tests in fake mode. The test host clears any provider key. |
| `python` | Locked sync, ruff (lint and format), pyright strict, and the Python integration tests in fake mode. |
| `cross-backend` | (L21) The real .NET and Python servers in fake mode, sent the same requests by `contracts/probes/run_probes.py`: every response is schema-validated, and the two backends must agree (see [0004](decisions/0004-sdk-comparison.md)). |
| `evaluations` | (L29) The Python evaluation package (`evaluations/python`): lint, format, strict types, dataset/result contract tests, dataset validation. The .NET evaluation runner and its tests build and run in the `dotnet` job; `cross-backend` also runs every dataset on both backends with the runner's launch mode (fresh store per write case, restarts, cancellation), writes the comparison, and uploads both. See [evaluations/README.md](../evaluations/README.md). |
| `repository-checks` | **The only required status check.** It always reports and passes only if every job above succeeded. A failed, cancelled, or skipped job makes it fail, so nothing can be silently skipped or left pending. |

A new job joins the required set by being listed in `repository-checks` → `needs`.

Run the same checks locally:

```bash
python3 scripts/check_docs_links.py && python3 -m unittest discover -s scripts/tests
python3 -m venv .venv && .venv/bin/pip install --require-hashes -r contracts/requirements.txt
.venv/bin/python -m unittest discover -s contracts/tests
.venv/bin/python -m unittest discover -s fixtures/tests
.venv/bin/python contracts/probes/run_probes.py --dotnet-build   # needs the .NET SDK and uv

dotnet restore maf-support-lab.slnx -p:ContinuousIntegrationBuild=true   # locked, like CI
dotnet build maf-support-lab.slnx --no-restore --configuration Release
dotnet test --solution maf-support-lab.slnx --no-build --configuration Release
```

## Reproducible restore

- **.NET:** each project except the AppHost commits a `packages.lock.json` with every direct and transitive package version and its content hash.
  - Locally, `dotnet restore` updates a lock file when you change a version in `Directory.Packages.props`. Commit the lock file with that change.
  - In CI (`ContinuousIntegrationBuild=true`), restore runs in locked mode. A version change without an updated lock file fails with `NU1004`.
  - The AppHost has no lock file: Aspire restores a different dashboard/orchestrator package per operating system, so a macOS lock cannot be restored on Linux. It is pinned by the exact `Aspire.AppHost.Sdk` version and central package versions.
- **Contract test tools:** `contracts/requirements.txt` is installed with `--require-hashes`.
- **SDK:** `global.json` pins .NET SDK 11.0.100 (patch roll-forward only); projects target `net11.0`.

## Security advisories (network-backed, not required)

Vulnerability data changes over time. In required builds, NuGet advisory warnings (NU1901–NU1904) are shown but don't fail the build, so a newly published advisory can't break unrelated pull requests. The separate `.github/workflows/dependency-audit.yml` fails when a vulnerable package (direct or transitive) is found. It runs weekly, on demand, and on pull requests that change package versions. Dependabot opens weekly update PRs for NuGet (grouped), contract test tools, and GitHub Actions.

## Live (opt-in)

Live checks are xUnit tests marked `Explicit = true` with `[Trait("Category", "Live")]`. A normal `dotnet test` reports them as "not run", and CI never runs them. They need `ANTHROPIC_API_KEY` and are skipped without it.

```bash
export ANTHROPIC_API_KEY=...
dotnet test --solution maf-support-lab.slnx -- --filter-trait "Category=Live" --explicit only
```

| Check | Since | Budget |
| --- | --- | --- |
| `LiveSmokeTests.Claude_answers_a_known_order_through_the_tool` | L05 | One turn on `claude-haiku-5-5`: two model calls (tool request, then answer), output capped at 4096 tokens. |

The model-judged quality lane (L32) is a separate live command, not a test, and in CI a manual-only workflow (L33, [reports](evaluations/reports.md)): `quality --live` in the evaluation runner. It has a 60-call budget, worst case 53. See [docs/evaluations/quality.md](evaluations/quality.md).

When a live check is run, record the date, model, package versions, number of calls, usage if available, and the result in the pull request, separately from the offline results.
