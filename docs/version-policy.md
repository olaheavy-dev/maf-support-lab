# Version and source policy

Planning snapshot checked 2026-11-09:

| Component | Candidate baseline | Status |
| --- | --- | --- |
| .NET MAF core, OpenAI, workflows | 1.28.0 | Released |
| .NET DevUI | 1.28.0-preview.261106.1 | Preview |
| .NET OpenAI hosting | 1.28.0-alpha.261106.1 | Alpha |
| Python MAF/core | 1.25.0 | Released, Python 3.11+ |
| Python DevUI | 1.0.0b261106 | Beta |

Selection rule: use the latest stable release of every runtime, framework, and package. Use a prerelease only when no stable release of that component exists, and record the reason in the version-baseline decision. The current selection is in [0001-version-baseline](decisions/0001-version-baseline.md).

These are observed release versions, not a restored-and-tested application dependency set. Releases are rechecked and runtimes selected first; the builds then prove compatible restores and commit the lock files. Provider integrations have independent versions. Do not assign the core version to every package.

Use official guidance for the design, release notes for migrations, and source/samples at the selected release tag to resolve API disagreements. Prove assumptions with the smallest relevant compile/import/integration check.

Record: checked date, runtime/SDK, direct package versions, transitive lockfile, provider/model configuration, preview rationale, docs/source links, and verification result.

Pin Aspire SDK/integrations and all application dependencies to compatible versions established during the scaffold ticket. Commit global.json, NuGet locks/central versions, and Python pyproject.toml/uv.lock.

Package refreshes are separate reviewed changes. Reading newer docs does not silently change an existing locked baseline.

## Release cooldown

Decided 2026-11-18 by the owner (prompt window: "Yes, 7 days everywhere (Recommended)" and "Yes, all three repos (Recommended)"); pnpm followed on 2026-11-18 (prompt window: "Yes, 7 days (Recommended)"). Dependabot proposals wait 7 days after a release, pnpm refuses npm versions younger than 7 days (`minimumReleaseAge: 10080` in `frontend/pnpm-workspace.yaml`, transitive packages included), and uv resolutions (both uv projects and the contracts lock) skip uploads younger than 7 days, so a malicious release that is pulled within hours stays out. Manual NuGet changes in `Directory.Packages.props` have no age guard. On 2026-11-17 `agent-framework` 1.26.1 and 1.26.2 were published to PyPI and removed within hours ([PYSEC-2026-4388](https://osv.dev/vulnerability/PYSEC-2026-4388), MAL-2026-20101).

- **Dependabot:** every update entry in `.github/dependabot.yml` sets `cooldown: default-days: 7` ([options reference](https://docs.github.com/en/code-security/dependabot/working-with-dependabot/dependabot-options-reference)). It applies to version updates only; Dependabot security updates are not delayed by it.
- **pnpm:** `minimumReleaseAge: 10080` (minutes; pnpm's default is 1440). An urgent fix is let through by listing the package or version under `minimumReleaseAgeExclude` ([pnpm settings](https://pnpm.io/settings/dependency-resolution)).
- **uv:** `exclude-newer = "7 days"` under `[tool.uv]` in `src/python/pyproject.toml` and `evaluations/python/pyproject.toml`, and in `contracts/uv.toml` for `uv pip compile` (which makes `contracts/requirements.txt`; uv finds that file by itself, checked on uv 0.13.7). `uv.lock` records it as `exclude-newer-span = "P7D"` ([uv settings](https://docs.astral.sh/uv/reference/settings/#exclude-newer)). Because `exclude-newer` also hides a fix uploaded in the last 7 days, a Dependabot security PR for a uv project to such a fix is probably held back too (not verified). For an urgent security fix, exempt one package with `exclude-newer-package = { <name> = false }`, with the reason in the pull request.

## Held upgrades

The latest-stable rule yields to compatibility: an upgrade waits while a tool or framework around it doesn't support it yet. Each hold is a Dependabot ignore rule (`.github/dependabot.yml`) or a pnpm setting (`frontend/pnpm-workspace.yaml`) with its reason, and is lifted as soon as its condition is met. The weekly maintenance routine re-checks them.

| Held | At | Waiting for | Since |
| --- | --- | --- | --- |
| Microsoft.Extensions.AI family | 10.x | Microsoft Agent Framework to depend on 11.x; then both move together (the owner's decision) | 2026-11-12 |
| TypeScript | 6.0.x | `typescript-eslint` to accept TypeScript 7 | 2026-11-12 |
| ESLint | 9.x | `eslint-config-next`'s plugins (`eslint-plugin-react`, `jsx-a11y`, `import`) to accept ESLint 10 | 2026-11-12 |
| OpenTelemetry JS SDK (frontend) | SDK 2.x / 0.2xx, API 1.9.x | `@vercel/otel` to accept 3.x / 0.3xx (and `sdk-trace-base` 3.x to exist). The API moves with them: the 2.x SDK requires `@opentelemetry/api <1.10.0` | 2026-11-12 |

Pinned around upstream gaps, rather than held:

| Setting | Why | Remove when |
| --- | --- | --- |
| `minimatch` override to 11.0.8 | GHSA-g3hv-75wg-m249; `typescript-eslint` still depends on `minimatch ^10` | `typescript-eslint` depends on `minimatch >=11.0.1` |
| `openapi-typescript` allowed on TypeScript 6 | Its `typescript ^5` peer range is stale; it generates the contract types byte-identically on 6, and CI diffs them | Its range includes 6 |
