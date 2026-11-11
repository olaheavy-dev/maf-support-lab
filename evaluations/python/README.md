# Python evaluation tooling (`support_evaluations`)

Evaluation-only Python code for the support backends, kept out of `src/python`'s manifest (L29). It validates the shared evaluation contracts the same way the .NET runner does; L31 adds native Python agent and workflow evaluations here.

```bash
cd evaluations/python
uv sync --locked
uv run python -m support_evaluations validate            # every dataset in ../datasets
uv run python -m support_evaluations validate ../results # and any run results written by the runner
uv run ruff check . && uv run pyright && uv run pytest
```

The datasets, schemas and the overall flow are described in [../README.md](../README.md).
