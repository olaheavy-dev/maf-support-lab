"""Loads every contract and fixture schema into one registry so cross-file $refs resolve offline."""

from __future__ import annotations

import json
from pathlib import Path

from jsonschema import Draft202012Validator
from referencing import Registry, Resource

ROOT = Path(__file__).resolve().parents[2]
SCHEMA_DIRS = (ROOT / "contracts" / "schemas", ROOT / "fixtures" / "schemas")
BASE = "https://contracts.maf-support-lab.example/v1/"


def load_json(path: Path):
    with path.open(encoding="utf-8") as handle:
        return json.load(handle)


def schema_files() -> list[Path]:
    return sorted(path for directory in SCHEMA_DIRS for path in directory.glob("*.schema.json"))


def build_registry() -> Registry:
    resources = []
    for path in schema_files():
        schema = load_json(path)
        resources.append((schema["$id"], Resource.from_contents(schema)))
    return Registry().with_resources(resources)


REGISTRY = build_registry()


def validator(name: str, definition: str | None = None) -> Draft202012Validator:
    """Validator for contracts/schemas/<name>.schema.json, or for one $defs entry of it."""
    uri = f"{BASE}{name}.schema.json"
    schema = {"$ref": f"{uri}#/$defs/{definition}"} if definition else REGISTRY.contents(uri)
    return Draft202012Validator(schema, registry=REGISTRY)
