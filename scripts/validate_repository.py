"""Validate canonical planning, dependency gates, and generated learning documents offline."""
from __future__ import annotations

import json
import re
from pathlib import Path, PurePosixPath
from urllib.parse import urlparse

from render_learning import REPO_URL, generated_documents

ROOT = Path(__file__).resolve().parents[1]


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(message)


def validate(root: Path = ROOT) -> tuple[int, int, int]:
    backlog = json.loads((root / "docs/learning/backlog.json").read_text())
    registry = json.loads((root / "docs/sources.json").read_text())
    mapping = json.loads((root / "docs/learning/github-issues.json").read_text())
    require(backlog["schema_version"] == 2, "Unsupported backlog schema")
    sources = registry["sources"]
    epics = {epic["id"] for epic in backlog["epics"]}
    require(len(epics) == len(backlog["epics"]), "Duplicate epic identifiers")
    seen: set[str] = set()
    available: set[str] = set()
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
        for field in ("implementation", "acceptance", "learning_outcomes", "interview_questions", "scope_exclusions"):
            require(len(ticket[field]) >= 2 and all(ticket[field]), f"Missing {field}: {ident}")
        require(bool(ticket["stretch"]) and bool(ticket["source_note"]), f"Missing learning detail: {ident}")
        require(bool(ticket["deliverables"]), f"Missing deliverables: {ident}")
        for target in ticket["deliverables"]:
            require(not PurePosixPath(target).is_absolute() and ".." not in PurePosixPath(target).parts,
                    f"Deliverable must be repository-relative: {ident}")
        missing = set(ticket["requires_capabilities"]) - available
        require(not missing, f"Unavailable prerequisite capabilities: {ident}: {sorted(missing)}")
        provided = ticket["provides_capabilities"]
        require(bool(provided) and len(set(provided)) == len(provided), f"Missing/duplicate produced capability: {ident}")
        require(not (set(provided) & available), f"Capability has multiple owners: {ident}")
        require(bool(ticket["course_refs"]) and bool(ticket["official_refs"]), f"Missing references: {ident}")
        for key in ticket["course_refs"] + ticket["official_refs"]:
            require(key in sources, f"Unregistered source {key}: {ident}")
        require(all(sources[key]["kind"] == "course" for key in ticket["course_refs"]),
                f"Invalid course reference: {ident}")
        require(all(sources[key]["kind"] in {"official-doc", "official-release"} for key in ticket["official_refs"]),
                f"Invalid official reference: {ident}")
        require(any(urlparse(sources[key]["url"]).hostname == "learn.microsoft.com" for key in ticket["official_refs"]),
                f"Missing Microsoft Learn page: {ident}")
        seen.add(ident)
        available.update(provided)

    for key, item in sources.items():
        parsed = urlparse(item["url"])
        require(parsed.scheme == "https" and bool(parsed.hostname), f"Invalid source URL: {key}")
        require(bool(item["checked_on"]), f"Missing checked date: {key}")
        if item["kind"] == "course":
            require(parsed.hostname == "github.com" and parsed.path.startswith("/Dometrain/"),
                    f"Course reference has wrong owner: {key}")
            require(re.search(r"/blob/[0-9a-f]{40}/", parsed.path) is not None,
                    f"Course source must be commit-pinned: {key}")
    for epic in epics:
        require(any(t["epic"] == epic for t in backlog["tickets"]), f"Empty epic: {epic}")

    require(mapping["repository"] == REPO_URL, "Unexpected GitHub issue repository")
    require(set(mapping["issues"]) == epics | seen, "GitHub mapping does not match ticket/epic IDs")
    numbers = [entry["number"] for entry in mapping["issues"].values()]
    require(len(numbers) == len(set(numbers)), "GitHub issue numbers must be unique")
    for key, entry in mapping["issues"].items():
        require(entry["html_url"] == f"{REPO_URL}/issues/{entry['number']}",
                f"GitHub issue URL mismatch: {key}")

    for relative, expected in generated_documents(backlog, registry, mapping).items():
        target = root / relative
        require(target.is_file() and target.read_text() == expected,
                f"Generated document is stale: {relative}; run python3 scripts/render_learning.py")
    expected_tickets = {f"{key}.md" for key in seen}
    actual_tickets = {p.name for p in (root / "docs/learning/tickets").glob("L*.md")}
    require(actual_tickets == expected_tickets, "Unexpected/missing generated ticket files")
    for relative in (
        "README.md", "AGENTS.md", "CONTRIBUTING.md", "SECURITY.md", ".gitignore",
        ".github/CODEOWNERS", ".github/pull_request_template.md",
        ".github/ISSUE_TEMPLATE/learning-ticket.yml", ".github/ISSUE_TEMPLATE/epic.yml",
        ".github/workflows/repository-checks.yml", "docs/version-policy.md",
        "docs/learning/alignment.md", "docs/learning/ticket-review.md",
        "docs/learning/evidence-template.md",
    ):
        require((root / relative).is_file(), f"Missing repository setup: {relative}")
    return len(epics), len(seen), len(sources)


def main() -> None:
    epics, tickets, sources = validate()
    print(f"Validated {epics} epics, {tickets} learning tickets, and {sources} source entries.")
    print("Dependency gates, issue mapping, and full generated learning documents are coherent.")
    print("These are planning checks; application behavior is verified by the learning tickets.")


if __name__ == "__main__":
    main()
