# Security

This repository holds a demonstration system: synthetic customer records and simulated business operations, with one fixed development identity per process. **It is not production-ready.** The [threat model](docs/threat-model.md) describes what is protected and how it is verified. Its [production requirements](docs/threat-model.md#production-requirements-explicit-follow-ups) list what a deployment would still need: authentication, rate limiting, retention, encryption, hosting hardening, real side effects, and operations.

## Reporting a vulnerability

Don't open a public issue with exploitable details. Use GitHub's private vulnerability reporting from the repository's **Security** tab.

## What protects the repository

| Protection | Where |
| --- | --- |
| Secret scanning and push protection (pushes containing a recognised credential are blocked) | Repository settings |
| `.env*`, `secrets.json`, local stores (`.data/`) and generated evaluation results are ignored | `.gitignore` |
| Locked dependencies: NuGet lock files, `uv.lock`, a hash-pinned `requirements.txt`; CI restores in locked mode | `Directory.Build.props`, `.github/workflows/repository-checks.yml` |
| Dependabot on every manifest: NuGet, .NET SDK, both uv projects, contract tools, GitHub Actions | `.github/dependabot.yml` |
| A weekly vulnerability audit of the NuGet and Python dependencies | `.github/workflows/dependency-audit.yml` |
| Code scanning: CodeQL on C#, Python and the workflows; .NET security analyzers as errors; ruff's Bandit rules | `.github/workflows/codeql.yml`, `Directory.Build.props`, `pyproject.toml` |
| GitHub Actions pinned by commit SHA, read-only by default, no persisted credentials | `.github/workflows/` |

## Credentials

Model keys come from the environment (`ANTHROPIC_API_KEY`) or the runtime's local secret store (.NET user-secrets). Never put them in files. Fake model mode, the default everywhere, needs no key. Keep examples as placeholders. Never commit real customer data, stored sessions, checkpoints, or captured prompts.

Dependency alerts and green checks help maintenance; they don't establish that the system is secure.
