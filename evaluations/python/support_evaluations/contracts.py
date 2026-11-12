"""The evaluation contracts: datasets in (evaluations/datasets), run results out (evaluations/results).

Both are versioned JSON validated against evaluations/schemas. On top of the schemas, two rules need code:
- case ids are unique within a dataset, and every `context` reference names a real fixture record;
- a run's summary counts agree with its results.
"""

from __future__ import annotations

import json
from collections import Counter
from functools import cache
from pathlib import Path
from typing import Any, cast

from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parents[3]
SCHEMAS = ROOT / "evaluations" / "schemas"
DATASETS = ROOT / "evaluations" / "datasets"
FIXTURES = ROOT / "fixtures" / "data"

# context "file:id" -> (fixture file, list key, id field); policies are keyed by name.
_FIXTURE_IDS = {
    "orders": ("orders.json", "orders", "order_id"),
    "invoices": ("invoices.json", "invoices", "invoice_id"),
    "payments": ("payments.json", "payments", "payment_id"),
    "knowledge-base": ("knowledge-base.json", "articles", "article_id"),
    "customers": ("customers.json", "customers", "customer_id"),
}


@cache
def _validator(name: str) -> Any:
    """Typed as Any: jsonschema's validator overloads are partly untyped (the backend's contract.py does the same)."""
    schema = json.loads((SCHEMAS / f"{name}.schema.json").read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema)


def _schema_errors(name: str, document: Any) -> list[str]:
    errors: list[Any] = list(_validator(name).iter_errors(document))
    errors.sort(key=lambda error: [str(part) for part in error.absolute_path])
    return [f"/{'/'.join(str(part) for part in error.absolute_path)}: {error.message}" for error in errors]


@cache
def fixture_ids() -> dict[str, frozenset[str]]:
    """Every record id that a dataset's `context` may name, from the shared fixtures."""
    ids: dict[str, frozenset[str]] = {}
    for kind, (file, key, field) in _FIXTURE_IDS.items():
        data: Any = json.loads((FIXTURES / file).read_text(encoding="utf-8"))
        records = cast(list[dict[str, Any]], data[key] if isinstance(data, dict) else data)
        ids[kind] = frozenset(str(record[field]) for record in records)
    policies = cast(dict[str, Any], json.loads((FIXTURES / "policies.json").read_text(encoding="utf-8")))
    ids["policies"] = frozenset(policies)
    return ids


def dataset_problems(document: Any) -> list[str]:
    """Schema violations, duplicate case ids, and context references to records that don't exist."""
    problems = _schema_errors("evaluation-dataset", document)
    if problems:
        return problems
    cases = cast(list[dict[str, Any]], document["cases"])
    duplicates = [case_id for case_id, count in Counter(c["case_id"] for c in cases).items() if count > 1]
    problems += [f"case_id '{case_id}' is used more than once" for case_id in duplicates]
    known = fixture_ids()
    for case in cases:
        for reference in cast(list[str], case.get("context", [])):
            kind, _, record = reference.partition(":")
            if record not in known.get(kind, frozenset()):
                problems.append(f"{case['case_id']}: context {reference} names no fixture record")
    return problems


def run_problems(document: Any) -> list[str]:
    """Schema violations, and a summary that doesn't add up to its results."""
    problems = _schema_errors("evaluation-run", document)
    if problems:
        return problems
    results = cast(list[dict[str, Any]], document["results"])
    counts = Counter(result["status"] for result in results)
    summary = cast(dict[str, int], document["summary"])
    expected = {
        "total": len(results),
        **{status: counts.get(status, 0) for status in ("passed", "failed", "error", "skipped")},
    }
    problems += [
        f"summary.{key} is {summary[key]}, results say {value}"
        for key, value in expected.items()
        if summary[key] != value
    ]
    return problems


def dataset_paths() -> list[Path]:
    return sorted(DATASETS.glob("*.json"))


def load(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))
