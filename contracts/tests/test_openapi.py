"""contracts/openapi.json, the client-generation view of the routes, stays in line with the contract."""

from __future__ import annotations

import re
import unittest

from schema_registry import ROOT, load_json

CONTRACTS = ROOT / "contracts"
OPENAPI = load_json(CONTRACTS / "openapi.json")
METHODS = {"get", "post", "put", "patch", "delete"}


def readme_routes() -> set[tuple[str, str]]:
    """The routes table in README.md, as (METHOD, path relative to /api)."""
    text = (CONTRACTS / "README.md").read_text(encoding="utf-8")
    found = re.findall(r"^\| `(GET|POST|PUT|PATCH|DELETE) /api(/v1/[^`]*)` \|", text, flags=re.MULTILINE)
    return {(method, path) for method, path in found}


def openapi_routes() -> set[tuple[str, str]]:
    return {
        (method.upper(), path)
        for path, item in OPENAPI["paths"].items()
        for method in item
        if method in METHODS
    }


def refs(node):
    if isinstance(node, dict):
        for key, value in node.items():
            if key == "$ref":
                yield value
            else:
                yield from refs(value)
    elif isinstance(node, list):
        for value in node:
            yield from refs(value)


class OpenApiTests(unittest.TestCase):
    def test_it_lists_exactly_the_readme_routes(self):
        routes = readme_routes()
        self.assertGreaterEqual(len(routes), 7)  # the README table parsed
        self.assertEqual(routes, openapi_routes())

    def test_every_reference_resolves(self):
        for ref in refs(OPENAPI):
            with self.subTest(ref=ref):
                if ref.startswith("#/"):
                    node = OPENAPI
                    for part in ref[2:].split("/"):
                        node = node[part]
                else:
                    self.assertTrue((CONTRACTS / ref).is_file(), ref)

    def test_every_request_and_response_schema_is_a_contract_schema(self):
        external = {ref for ref in refs(OPENAPI) if not ref.startswith("#/")}
        self.assertTrue(all(ref.startswith("./schemas/") and ref.endswith(".schema.json") for ref in external), external)


if __name__ == "__main__":
    unittest.main()
