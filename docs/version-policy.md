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
