"""The shared version-1 contract (contracts/schemas), loaded once and used unchanged (never loosened to fit the SDK).

Request bodies are validated against these schemas before anything runs, and triage output is validated against the
full triage-decision schema. Errors are reported as JSON pointers, as the contract's invalid_request problem expects.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from functools import cache
from typing import Any, cast

from jsonschema import Draft202012Validator
from referencing import Registry, Resource

from support_agent.fixtures import CONTRACTS

CONTRACT_VERSION = "1.1"
BACKEND = "python"


@dataclass(frozen=True, slots=True)
class SchemaError:
    pointer: str
    message: str


@cache
def _registry() -> tuple[Registry[Any], dict[str, dict[str, Any]]]:
    schemas: dict[str, dict[str, Any]] = {}
    resources: list[tuple[str, Resource[Any]]] = []
    for path in sorted(CONTRACTS.glob("*.schema.json")):
        schema: dict[str, Any] = json.loads(path.read_text(encoding="utf-8"))
        schemas[path.name.removesuffix(".schema.json")] = schema
        resources.append((schema["$id"], Resource.from_contents(schema)))
    registry: Registry[Any] = Registry[Any]().with_resources(resources)
    return registry, schemas


def errors(schema_name: str, document: Any) -> list[SchemaError]:
    """The ways the document breaks the named contract schema (empty when valid)."""
    registry, schemas = _registry()
    validator = cast(
        Any, Draft202012Validator(schemas[schema_name], registry=registry)
    )  # jsonschema's stubs are partial
    found: list[SchemaError] = []
    problems: list[Any] = list(validator.iter_errors(document))
    for error in sorted(problems, key=lambda e: [str(part) for part in e.absolute_path]):
        pointer = "".join(f"/{part}" for part in error.absolute_path)
        found.append(SchemaError(pointer or "/", error.message[:300]))
    return found


def is_valid(schema_name: str, document: Any) -> bool:
    return not errors(schema_name, document)
