"""Regression checks for real planning failures found during ticket review."""
from __future__ import annotations

import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))
from validate_repository import ROOT, validate


class LearningValidationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "repository"
        shutil.copytree(ROOT, self.root, ignore=shutil.ignore_patterns(".git", "__pycache__", ".venv", "bin", "obj", ".data", ".pytest_cache", ".ruff_cache", "node_modules"))

    def tearDown(self) -> None:
        self.temp.cleanup()

    def edit_json(self, relative: str, edit) -> None:
        target = self.root / relative
        data = json.loads(target.read_text())
        edit(data)
        target.write_text(json.dumps(data, indent=2) + "\n")

    def test_current_plan_is_valid(self) -> None:
        epics, tickets, _ = validate(self.root)
        self.assertEqual((epics, tickets), (8, 38))  # 5 tickets and 1 epic retired on 2026-11-11

    def test_prebuild_evaluation_deliverable_is_rejected(self) -> None:
        self.edit_json("docs/learning/backlog.json",
            lambda b: next(t for t in b["tickets"] if t["id"] == "L14")["deliverables"].append("evaluations/datasets/"))
        with self.assertRaisesRegex(SystemExit, "Evaluation deliverable before application completion: L14"):
            validate(self.root)

    def test_unnumbered_decision_record_is_rejected(self) -> None:
        self.edit_json("docs/learning/backlog.json",
            lambda b: next(t for t in b["tickets"] if t["id"] == "L34").update(deliverables=["docs/decisions/documentation-index.md"]))
        with self.assertRaisesRegex(SystemExit, "Decision record must have a numbered ADR filename: L34"):
            validate(self.root)

    def test_numbered_adr_authoring_order_is_enforced(self) -> None:
        def reverse_adrs(backlog):
            for ticket in backlog["tickets"]:
                ticket["deliverables"] = [p.replace("0003-state-and-idempotency", "0005-state-and-idempotency") for p in ticket["deliverables"]]
        self.edit_json("docs/learning/backlog.json", reverse_adrs)
        with self.assertRaisesRegex(SystemExit, "Numbered ADR introduced out of order: L21"):
            validate(self.root)

    def test_adr_number_cannot_name_two_different_decisions(self) -> None:
        self.edit_json("docs/learning/backlog.json",
            lambda b: next(t for t in b["tickets"] if t["id"] == "L21")["deliverables"].append("docs/decisions/0002-other-decision.md"))
        with self.assertRaisesRegex(SystemExit, "ADR number reused for a different decision: L21"):
            validate(self.root)

    def test_future_authorization_dependency_is_rejected(self) -> None:
        self.edit_json("docs/learning/backlog.json",
            lambda b: next(t for t in b["tickets"] if t["id"] == "L10")["requires_capabilities"].append("dotnet_access_regressions"))
        with self.assertRaisesRegex(SystemExit, "Unavailable prerequisite capabilities: L10"):
            validate(self.root)

    def test_duplicate_capability_owner_is_rejected(self) -> None:
        self.edit_json("docs/learning/backlog.json",
            lambda b: next(t for t in b["tickets"] if t["id"] == "L17")["provides_capabilities"].append("apphost"))
        with self.assertRaisesRegex(SystemExit, "Capability has multiple owners: L17"):
            validate(self.root)

    def test_ticket_acceptance_cannot_silently_drift(self) -> None:
        target = self.root / "docs/learning/tickets/L08.md"
        target.write_text(target.read_text().replace("No mutating tool is registered", "A mutating tool is registered"))
        with self.assertRaisesRegex(SystemExit, "Generated document is stale: docs/learning/tickets/L08.md"):
            validate(self.root)

    def test_wrong_source_classification_is_rejected(self) -> None:
        self.edit_json("docs/learning/backlog.json",
            lambda b: b["tickets"][0]["official_refs"].append("dn-program"))
        with self.assertRaisesRegex(SystemExit, "Invalid official reference: L01"):
            validate(self.root)

    def test_wrong_issue_link_is_rejected(self) -> None:
        self.edit_json("docs/learning/github-issues.json",
            lambda m: m["issues"]["L01"].update(html_url="https://github.com/example/wrong/issues/8"))
        with self.assertRaisesRegex(SystemExit, "GitHub issue URL mismatch: L01"):
            validate(self.root)

    def test_epic_exit_criteria_cannot_silently_drift(self) -> None:
        target = self.root / "docs/learning/epics/E02.md"
        target.write_text(target.read_text().replace("read-only", "mutating"))
        with self.assertRaisesRegex(SystemExit, "Generated document is stale: docs/learning/epics/E02.md"):
            validate(self.root)

    def test_existing_ticket_ids_survive_reordering(self) -> None:
        backlog = json.loads((self.root / "docs/learning/backlog.json").read_text())
        moved = next(t for t in backlog["tickets"] if t["id"] == "L16")
        self.assertNotEqual(moved["order"], 16)
        validate(self.root)

    def test_evaluation_cannot_move_before_application_build_completion(self) -> None:
        def move_early(backlog):
            tickets = backlog["tickets"]
            moved = next(t for t in tickets if t["id"] == "L29")
            tickets.remove(moved)
            tickets.insert(next(i for i,t in enumerate(tickets) if t["id"] == "L21"), moved)
            for i,t in enumerate(tickets,1):
                t["order"] = i
                t["depends_on"] = [] if i == 1 else [tickets[i-2]["id"]]
        self.edit_json("docs/learning/backlog.json", move_early)
        with self.assertRaisesRegex(SystemExit, "Post-build phase before application completion: L29"):
            validate(self.root)

    def test_documentation_cannot_move_before_application_build_completion(self) -> None:
        def move_early(backlog):
            tickets = backlog["tickets"]
            moved = next(t for t in tickets if t["id"] == "L34")
            tickets.remove(moved)
            tickets.insert(next(i for i,t in enumerate(tickets) if t["id"] == "L21"), moved)
            for i,t in enumerate(tickets,1):
                t["order"] = i
                t["depends_on"] = [] if i == 1 else [tickets[i-2]["id"]]
        self.edit_json("docs/learning/backlog.json", move_early)
        with self.assertRaisesRegex(SystemExit, "Post-build phase before application completion: L34"):
            validate(self.root)

    def test_a_retired_id_cannot_be_reused(self) -> None:
        def reuse(backlog):
            backlog["retired_tickets"][0]["id"] = "L21"  # also a live ticket
        self.edit_json("docs/learning/backlog.json", reuse)
        with self.assertRaisesRegex(SystemExit, "A ticket is both live and retired|Duplicate"):
            validate(self.root)

    def test_a_live_ticket_cannot_depend_on_a_retired_one(self) -> None:
        self.edit_json("docs/learning/backlog.json",
            lambda b: next(t for t in b["tickets"] if t["id"] == "L29")["depends_on"].append("L24"))
        with self.assertRaisesRegex(SystemExit, "Unknown/future dependency: L29"):
            validate(self.root)

    def test_a_retired_ticket_needs_a_reason(self) -> None:
        self.edit_json("docs/learning/backlog.json", lambda b: b["retired_tickets"][1].update(reason=""))
        with self.assertRaisesRegex(SystemExit, "Retired item needs a title and reason: L23"):
            validate(self.root)

    def test_dropping_a_retired_id_breaks_the_stable_sequence(self) -> None:
        self.edit_json("docs/learning/backlog.json", lambda b: b["retired_tickets"].pop())
        with self.assertRaisesRegex(SystemExit, "Stable ticket IDs must remain unique and complete"):
            validate(self.root)


if __name__ == "__main__":
    unittest.main()
