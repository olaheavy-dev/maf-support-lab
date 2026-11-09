"""Validate repository planning metadata without network access or model credentials."""
from __future__ import annotations

import json
import re
from pathlib import Path
from urllib.parse import urlparse

ROOT = Path(__file__).resolve().parents[1]


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(message)


def main() -> None:
    backlog = json.loads((ROOT / "docs/learning/backlog.json").read_text())
    registry = json.loads((ROOT / "docs/sources.json").read_text())
    sources = registry["sources"]
    epics = {epic["id"] for epic in backlog["epics"]}
    require(len(epics) == len(backlog["epics"]), "Duplicate epic identifiers")
    seen: set[str] = set()
    for order, ticket in enumerate(backlog["tickets"], 1):
        ident = ticket["id"]
        require(ident == f"L{order:02}", f"Noncontiguous learning sequence: {ident}")
        require(ident not in seen and ticket["order"] == order, f"Invalid order: {ident}")
        require(ticket["epic"] in epics, f"Unknown parent epic: {ident}")
        require(all(dep in seen for dep in ticket["depends_on"]), f"Unknown/future dependency: {ident}")
        if order == 1:
            require(not ticket["depends_on"], "First ticket must be unblocked")
        else:
            require(f"L{order-1:02}" in ticket["depends_on"], f"Missing sequential prerequisite: {ident}")
        for field in ("implementation", "acceptance", "learning_outcomes", "interview_questions"):
            require(len(ticket[field]) >= 2 and all(ticket[field]), f"Missing {field}: {ident}")
        require(bool(ticket["stretch"]) and bool(ticket["source_note"]), f"Missing learning detail: {ident}")
        require(len(ticket["course_refs"]) >= 1 and len(ticket["official_refs"]) >= 1, f"Missing references: {ident}")
        for key in ticket["course_refs"] + ticket["official_refs"]:
            require(key in sources, f"Unregistered source {key}: {ident}")
        require(all(sources[key]["kind"] == "course" for key in ticket["course_refs"]), f"Invalid course reference: {ident}")
        require(any(urlparse(sources[key]["url"]).hostname == "learn.microsoft.com" for key in ticket["official_refs"]), f"Missing Microsoft Learn page: {ident}")
        rendered = ROOT / "docs/learning/tickets" / f"{ident}.md"
        require(rendered.is_file(), f"Missing readable ticket: {ident}")
        content = rendered.read_text()
        require(all(sources[key]["url"] in content for key in ticket["course_refs"] + ticket["official_refs"]), f"Ticket links out of sync: {ident}")
        seen.add(ident)

    for key, item in sources.items():
        parsed = urlparse(item["url"])
        require(parsed.scheme == "https" and bool(parsed.hostname), f"Invalid source URL: {key}")
        require(bool(item["checked_on"]), f"Missing checked date: {key}")
        if item["kind"] == "course":
            require(re.search(r"/blob/[0-9a-f]{40}/", parsed.path) is not None, f"Course source must be commit-pinned: {key}")
    for epic in epics:
        require(any(t["epic"] == epic for t in backlog["tickets"]), f"Empty epic: {epic}")
    for relative in (
        "README.md", "AGENTS.md", "CONTRIBUTING.md", "SECURITY.md", ".gitignore",
        ".github/CODEOWNERS", ".github/pull_request_template.md",
        ".github/ISSUE_TEMPLATE/learning-ticket.yml", ".github/ISSUE_TEMPLATE/epic.yml",
        ".github/workflows/repository-checks.yml", "docs/version-policy.md",
        "docs/learning/roadmap.md", "docs/learning/evidence-template.md",
    ):
        require((ROOT / relative).is_file(), f"Missing repository setup: {relative}")
    print(f"Validated {len(epics)} epics, {len(seen)} learning tickets, and {len(sources)} source entries.")
    print("Repository metadata checks passed. Application build/tests are introduced by the learning tickets.")


if __name__ == "__main__":
    main()
