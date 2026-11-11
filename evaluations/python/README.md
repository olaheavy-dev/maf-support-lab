# Python evaluation tooling (`support_evaluations`)

Evaluation-only Python code for the support backends, kept out of `src/python`'s manifest (L29). It validates the shared evaluation contracts the same way the .NET runner does, and runs native Python agent and workflow evaluation with MAF 1.25.0 (L31).

```bash
cd evaluations/python
uv sync --locked
uv run python -m support_evaluations validate            # every dataset in ../datasets
uv run python -m support_evaluations validate ../results # and any run results written by the runner
uv run python -m support_evaluations native evaluate   # score ../recordings/python without calling the model
uv run python -m support_evaluations native record     # re-record after a dataset version changes
uv run python -m support_evaluations native workflow   # evaluate_workflow on a one-agent workflow
uv run ruff check . && uv run pyright && uv run pytest
```

The datasets, schemas and the overall flow are described in [../README.md](../README.md).
