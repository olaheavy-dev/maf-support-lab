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
        shutil.copytree(ROOT, self.root, ignore=shutil.ignore_patterns(".git", "__pycache__"))

    def tearDown(self) -> None:
        self.temp.cleanup()

    def edit_json(self, relative: str, edit) -> None:
        target = self.root / relative
        data = json.loads(target.read_text())
        edit(data)
        target.write_text(json.dumps(data, indent=2) + "\n")

    def test_current_plan_is_valid(self) -> None:
        epics, tickets, _ = validate(self.root)
        self.assertEqual((epics, tickets), (7, 28))

    def test_future_authorization_dependency_is_rejected(self) -> None:
        self.edit_json("docs/learning/backlog.json",
            lambda b: b["tickets"][9]["requires_capabilities"].append("dotnet_access_regressions"))
        with self.assertRaisesRegex(SystemExit, "Unavailable prerequisite capabilities: L10"):
            validate(self.root)

    def test_duplicate_capability_owner_is_rejected(self) -> None:
        self.edit_json("docs/learning/backlog.json",
            lambda b: b["tickets"][16]["provides_capabilities"].append("apphost"))
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


if __name__ == "__main__":
    unittest.main()
