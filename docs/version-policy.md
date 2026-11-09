# Version and source policy

Planning snapshot checked 2026-11-09:

| Component | Candidate baseline | Status |
| --- | --- | --- |
| .NET MAF core, OpenAI, workflows | 1.28.0 | Released |
| .NET DevUI | 1.28.0-preview.261106.1 | Preview |
| .NET OpenAI hosting | 1.28.0-alpha.261106.1 | Alpha |
| Python MAF/core | 1.25.0 | Released, Python 3.11+ |
| Python DevUI | 1.0.0b261106 | Beta |

These are observed release versions, not a restored-and-tested application dependency set. L01 rechecks releases and selects runtimes; L03/L04 prove compatible restores/builds and commit locks. Provider integrations have independent versions. Do not assign the core version to every package.

Use official guidance for the design, release notes for migrations, and source/samples at the selected release tag to resolve API disagreements. Prove assumptions with the smallest relevant compile/import/integration check.

Record: checked date, runtime/SDK, direct package versions, transitive lockfile, provider/model configuration, preview rationale, docs/source links, and verification result.

Pin Aspire SDK/integrations and all application dependencies to compatible versions established during the scaffold ticket. Commit global.json, NuGet locks/central versions, Python pyproject.toml/uv.lock, and the frontend lockfile when introduced.

Package refreshes are separate reviewed changes. Reading newer docs does not silently change an existing locked baseline.
