"""Contract checks: schemas are well formed, examples behave as declared, and streams obey the terminal-event rules."""

from __future__ import annotations

import unittest

from jsonschema import Draft202012Validator

from schema_registry import ROOT, load_json, schema_files, validator

EXAMPLES = ROOT / "contracts" / "examples"
INDEX = load_json(EXAMPLES / "index.json")["examples"]

# Cases the L02 acceptance criteria require the examples to cover.
REQUIRED_COVERAGE = {
    "success",
    "missing_order",
    "unknown_order",
    "invalid_route",
    "forbidden_access",
    "approval_denial",
    "operation_status",
    "stream_pause",
    "stream_error",
    "duplicate_submission",
}


class SchemaTests(unittest.TestCase):
    def test_every_schema_is_valid_draft_2020_12(self):
        for path in schema_files():
            with self.subTest(schema=path.name):
                Draft202012Validator.check_schema(load_json(path))

    def test_every_schema_and_example_file_is_indexed_or_used(self):
        indexed = {entry["file"] for entry in INDEX}
        on_disk = {str(path.relative_to(EXAMPLES)) for path in EXAMPLES.glob("*/*.json")}
        self.assertEqual(on_disk, indexed, "examples/index.json must list every example file")


class ExampleTests(unittest.TestCase):
    def test_valid_examples_pass_and_invalid_examples_fail(self):
        for entry in INDEX:
            documents = load_json(EXAMPLES / entry["file"])
            documents = documents if entry.get("stream") else [documents]
            check = validator(entry["schema"])
            for position, document in enumerate(documents):
                errors = list(check.iter_errors(document))
                with self.subTest(example=entry["file"], item=position):
                    if entry["valid"]:
                        self.assertEqual([], [error.message for error in errors])
                    else:
                        self.assertTrue(errors, f"expected rejection: {entry['reason']}")

    def test_invalid_examples_state_why_they_must_fail(self):
        for entry in INDEX:
            if not entry["valid"]:
                with self.subTest(example=entry["file"]):
                    self.assertTrue(entry.get("reason"))

    def test_acceptance_cases_have_valid_and_rejected_examples_where_needed(self):
        covered = {tag for entry in INDEX if entry["valid"] for tag in entry["covers"]}
        self.assertEqual(set(), REQUIRED_COVERAGE - covered)
        rejected = {tag for entry in INDEX if not entry["valid"] for tag in entry["covers"]}
        for tag in ("invalid_route", "forbidden_access", "duplicate_submission"):
            self.assertIn(tag, rejected)


class StreamRuleTests(unittest.TestCase):
    """Rules a JSON Schema cannot express across several events."""

    def streams(self):
        return [entry for entry in INDEX if entry.get("stream") and entry["valid"]]

    def test_each_stream_has_exactly_one_terminal_event_and_it_is_last(self):
        for entry in self.streams():
            events = load_json(EXAMPLES / entry["file"])
            types = [event["type"] for event in events]
            with self.subTest(stream=entry["file"]):
                self.assertEqual(1, types.count("turn.final"))
                self.assertEqual("turn.final", types[-1])
                self.assertEqual("turn.started", types[0])

    def test_sequence_increases_by_one_and_ids_stay_constant(self):
        for entry in self.streams():
            events = load_json(EXAMPLES / entry["file"])
            with self.subTest(stream=entry["file"]):
                self.assertEqual(list(range(1, len(events) + 1)), [event["sequence"] for event in events])
                self.assertEqual(1, len({(event["conversation_id"], event["turn_id"]) for event in events}))
                final = events[-1]["data"]["result"]
                self.assertEqual((events[0]["conversation_id"], events[0]["turn_id"]), (final["conversation_id"], final["turn_id"]))

    def test_streamed_text_matches_the_final_reply(self):
        # 1.1: only the deltas after the last reply.reset count. A final result without a reply leaves no counted text.
        # A stream that sent no text at all (a replayed duplicate, for example) has nothing to check.
        for entry in self.streams():
            events = load_json(EXAMPLES / entry["file"])
            resets = [index for index, event in enumerate(events) if event["type"] == "reply.reset"]
            counted = events[resets[-1] + 1:] if resets else events
            deltas = "".join(event["data"]["text"] for event in counted if event["type"] == "reply.delta")
            reply = events[-1]["data"]["result"].get("reply")
            if not any(event["type"] in ("reply.delta", "reply.reset") for event in events):
                continue
            with self.subTest(stream=entry["file"]):
                self.assertEqual(reply["text"] if reply else "", deltas)


class TransportSeparationTests(unittest.TestCase):
    def test_turn_failures_and_http_errors_use_separate_code_sets(self):
        common = load_json(ROOT / "contracts" / "schemas" / "common.schema.json")
        problem = load_json(ROOT / "contracts" / "schemas" / "problem.schema.json")
        turn_codes = set(common["$defs"]["turn_error"]["properties"]["code"]["enum"])
        http_codes = set(problem["properties"]["code"]["enum"])
        # rate_limited and internal_error may legitimately happen at either layer.
        self.assertEqual({"rate_limited", "internal_error"}, turn_codes & http_codes)

    def test_requests_never_carry_identity_or_confirmation(self):
        forbidden = {"customer_id", "principal", "user_id", "customer_confirmed", "approved", "arguments"}
        for name in ("turn-request", "approval-decision-request", "create-conversation-request"):
            schema = load_json(ROOT / "contracts" / "schemas" / f"{name}.schema.json")
            with self.subTest(schema=name):
                self.assertFalse(schema["additionalProperties"])
                self.assertEqual(set(), forbidden & set(schema.get("properties", {})))


if __name__ == "__main__":
    unittest.main()
