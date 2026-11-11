"""Checks the cross-backend probe runner's own logic (L21) without starting any server.

The probes are only useful if their comparison can fail: these tests feed it synthetic observations with a known
contract or ownership regression and expect it to be reported, and check that what is normalized away (runtime ids,
timestamps, wording) is only what may legitimately differ.
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "probes"))

import run_probes  # noqa: E402
from run_probes import ContractViolation, Normalizer, compare, parse_sse  # noqa: E402


def observed(**probes: object) -> dict[str, object]:
    return dict(probes)


class NormalizerTests(unittest.TestCase):
    def test_runtime_ids_become_stable_placeholders(self) -> None:
        normalize = Normalizer()
        first = normalize({"turn_id": "turn_abc123", "operation_ids": ["op_x1", "op_x1"], "again": "turn_abc123"})
        self.assertEqual(first, {"again": "<turn:1>", "operation_ids": ["<op:1>", "<op:1>"], "turn_id": "<turn:1>"})
        self.assertEqual(normalize("turn_other"), "<turn:2>")

    def test_fixture_ids_and_business_values_are_kept(self) -> None:
        normalize = Normalizer()
        body = {"order_id": "ORD-1003", "order_status": "cancelled", "payment_id": "PAY-7101", "code": "not_found"}
        self.assertEqual(normalize(body), body)

    def test_wording_times_and_trace_ids_are_masked_but_errors_must_exist(self) -> None:
        normalize = Normalizer()
        body = {
            "detail": "Any words",
            "reply": {"kind": "answer", "text": "Any words"},
            "created_at": "2026-11-11T10:00:00.123Z",
            "trace_id": "0af7651916cd43dd8448eb211c80319c",
            "errors": [{"pointer": "/x", "message": "m"}],
        }
        self.assertEqual(
            normalize(body),
            {
                "created_at": "<time>",
                "detail": "<text>",
                "errors": "<errors>",
                "reply": {"kind": "answer", "text": "<text>"},
                "trace_id": "<trace>",
            },
        )
        self.assertEqual(normalize({"errors": []}), {"errors": "<no errors>"})


class ComparisonTests(unittest.TestCase):
    def test_identical_observations_agree(self) -> None:
        same = {"status": 404, "body": {"code": "not_found"}}
        self.assertEqual(compare({"dotnet": observed(p=same), "python": observed(p=same)}), [])

    def test_an_ownership_regression_is_reported(self) -> None:
        denied = {"status": 404, "body": {"code": "not_found"}}
        leaked = {"status": 200, "body": {"conversation_id": "<conv:1>"}}
        found = compare({"dotnet": observed(**{"get-conversation-as-other": denied}), "python": observed(**{"get-conversation-as-other": leaked})})
        self.assertTrue(any(line.startswith("get-conversation-as-other/status") for line in found), found)

    def test_a_contract_difference_in_codes_or_fields_is_reported(self) -> None:
        dotnet = observed(p={"body": {"outcome": "failed", "route": {"status": "routed", "target": "orders"}}})
        python = observed(p={"body": {"outcome": "failed"}})
        self.assertEqual(compare({"dotnet": dotnet, "python": python}), ['p/body/route: only dotnet has it ({"status": "routed", "target": "orders"})'])

    def test_a_probe_only_one_backend_ran_is_reported(self) -> None:
        self.assertTrue(compare({"dotnet": observed(p={"status": 200}), "python": observed()}))

    def test_nothing_is_allowed_to_differ_by_default(self) -> None:
        self.assertEqual(run_probes.ALLOWED_DIFFERENCES, {})


class StreamTests(unittest.TestCase):
    @staticmethod
    def event(sequence: int, kind: str, data: dict[str, object]) -> str:
        import json

        body = {"contract_version": "1.1", "type": kind, "sequence": sequence, "conversation_id": "conv_abcdefgh", "turn_id": "turn_abcdefgh", "data": data}
        return f"event: {kind}\nid: {sequence}\ndata: {json.dumps(body)}\n\n"

    def test_a_stream_without_a_final_event_is_a_contract_violation(self) -> None:
        text = self.event(1, "turn.started", {"client_turn_id": "3f2c8a1e-5b7d-4c9a-8e2f-1a6b9c0d4e71"})
        with self.assertRaises(ContractViolation):
            parse_sse(text, "stream")

    def test_an_event_that_breaks_the_schema_is_a_contract_violation(self) -> None:
        with self.assertRaises(ContractViolation):
            parse_sse(self.event(1, "turn.started", {"unexpected": True}), "stream")


if __name__ == "__main__":
    unittest.main()
