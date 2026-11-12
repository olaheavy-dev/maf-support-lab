# Contributing

The working agreement is in [AGENTS.md](AGENTS.md): branches, pull requests, review, and the engineering rules. This page covers the practical side.

## Before opening a pull request

Run the same checks CI runs ([runbook](docs/runbook.md#checks-as-ci-runs-them)):

```bash
python3 scripts/check_docs_links.py && python3 -m unittest discover -s scripts/tests
dotnet build maf-support-lab.slnx -warnaserror && dotnet test --solution maf-support-lab.slnx
(cd src/python && uv sync --locked && uv run ruff check . && uv run ruff format --check . && uv run pyright && uv run pytest)
(cd evaluations/python && uv run --locked pytest)
```

Behaviour that both backends share also needs the cross-backend probes and the evaluation datasets ([testing](docs/testing.md), [evaluations](evaluations/README.md)).

## Dependencies

- **.NET:** versions live in `Directory.Packages.props`. After changing one, run `dotnet restore` and commit the updated `packages.lock.json` files; CI restores in locked mode.
- **Python:** change `pyproject.toml`, run `uv lock`, and commit `uv.lock`; CI syncs with `--locked`.
- Dependabot proposes updates weekly. Treat a runtime or major-version move as its own reviewed change.

## Pull requests

Describe the change, how it was verified (commands, results, and the failure cases that were tried), and anything that could break. Live model runs are optional, and their results are reported separately from the offline checks. Security issues go through the private route in [SECURITY.md](SECURITY.md), not a public issue.
