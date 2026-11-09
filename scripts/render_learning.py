"""Render the learning documents from one canonical backlog; never edit GitHub."""
from __future__ import annotations

import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REPO_URL = "https://github.com/sunkanmi-olawuwo/maf-support-lab"


def code(text: str) -> str:
    return chr(96) + text + chr(96)


def issue_link(key: str, mapping: dict) -> str:
    item = mapping.get("issues", {}).get(key)
    return f"[{key} (#{item['number']})]({item['html_url']})" if item else key


def render_ticket(ticket: dict, sources: dict, mapping: dict, checked_on: str) -> str:
    lines = [
        f"# [{ticket['id']}] {ticket['title']}", "",
        f"Parent epic: {issue_link(ticket['epic'], mapping)}. Stack: {ticket['stack']}. Order: {ticket['order']}.", "",
        "## Prerequisites", "",
        ", ".join(issue_link(dep, mapping) for dep in ticket["depends_on"]) or "None. This is the first learning ticket.", "",
        "Required capabilities: " + (", ".join(code(c) for c in ticket["requires_capabilities"]) or "None.") + "", "",
        f"Capability ownership and cross-ticket rules: [alignment contract]({REPO_URL}/blob/main/docs/learning/alignment.md) and [capability map]({REPO_URL}/blob/main/docs/learning/capabilities.md).", "",
        "## What I will implement", "",
    ]
    lines.extend(f"- [ ] {step}" for step in ticket["implementation"])
    lines += ["", "## Expected deliverables", "",
        "Planned locations; introduce or update the relevant files here. These paths do not claim implementation already exists.", ""]
    lines.extend(f"- {code(path)}" for path in ticket["deliverables"])
    lines += ["", "## Scope boundaries", ""]
    lines.extend(f"- Outside this ticket: {item}." for item in ticket["scope_exclusions"])
    lines += ["", "## References: course files", ""]
    lines.extend(f"- [{sources[key]['title']}]({sources[key]['url']})" for key in ticket["course_refs"])
    lines += ["", ticket["source_note"], "", "## References: official docs and releases", ""]
    lines.extend(f"- [{sources[key]['title']}]({sources[key]['url']})" for key in ticket["official_refs"])
    lines += ["",
        f"Reference registry checked {checked_on}. Before coding, recheck live docs against the selected release and record exact versions and any disagreement. See [version policy]({REPO_URL}/blob/main/docs/version-policy.md).",
        "", "## Acceptance criteria", ""]
    lines.extend(f"- [ ] {step}" for step in ticket["acceptance"])
    lines += ["", "## What I should learn", ""]
    lines.extend(f"- {item}" for item in ticket["learning_outcomes"])
    lines += ["", "## Interview practice", ""]
    lines.extend(f"{i}. {question}" for i, question in enumerate(ticket["interview_questions"], 1))
    lines += ["", "## Independent stretch exercise", "", ticket["stretch"],
        "", "The stretch is optional. Core acceptance criteria define completion; do not add future-ticket dependencies just to complete a stretch.",
        "", "## Evidence before closing", "",
        "- [ ] Link the PR and verification commands/results.",
        f"- [ ] Add {code('docs/learning/evidence/' + ticket['id'] + '.md')} using the evidence template.",
        "- [ ] Include sanitized traces/screenshots where relevant; otherwise state why they are unnecessary.",
        "- [ ] Answer the interview questions in my own words and record one tradeoff or remaining question.",
        "", "Capabilities produced when accepted: " + ", ".join(code(c) for c in ticket["provides_capabilities"]) + ".",
        "", "Coaching mode: I attempt the task first; the assistant supplies hints, debugging help, or review unless I explicitly request implementation.",
        "", "Generated from docs/learning/backlog.json and docs/sources.json. Change those sources and rerender; preserve actual learning progress when synchronizing the GitHub issue.",
    ]
    return "\n".join(lines) + "\n"


def render_epic(epic: dict, tickets: list[dict], mapping: dict) -> str:
    lines = [f"# [{epic['id']}] {epic['title']}", "", "## Outcome", "", epic["outcome"],
        "", "## Exit criteria", "", epic["exit_criteria"], "", "## Ordered learning tickets", ""]
    for ticket in tickets:
        if ticket["epic"] == epic["id"]:
            lines.append(f"- [ ] {issue_link(ticket['id'], mapping)} — {ticket['title']}")
    lines += ["", f"Follow the [learning workflow]({REPO_URL}/blob/main/docs/learning/workflow.md) and [alignment contract]({REPO_URL}/blob/main/docs/learning/alignment.md). Close this epic only after its child tickets and demonstrated exit criteria are complete."]
    return "\n".join(lines) + "\n"


def generated_documents(backlog: dict, registry: dict, mapping: dict) -> dict[str, str]:
    tickets = backlog["tickets"]
    docs = {
        f"docs/learning/tickets/{t['id']}.md": render_ticket(t, registry["sources"], mapping, registry["checked_on"])
        for t in tickets
    }
    docs.update({
        f"docs/learning/epics/{e['id']}.md": render_epic(e, tickets, mapping)
        for e in backlog["epics"]
    })
    lines = ["# Ordered learning roadmap", "",
        "Work one ticket at a time. Stable ticket IDs and issue links are retained. Only the first unblocked ticket is Ready.",
        "", "The [alignment contract](alignment.md) fixes cross-ticket scope and the [capability map](capabilities.md) identifies prerequisites. Source metadata lives in backlog.json and ../sources.json; generated ticket/epic pages must be rerendered when it changes.",
        "", "| Order | Epic | Learning ticket | Stack | Depends on |",
        "| --- | --- | --- | --- | --- |"]
    for ticket in tickets:
        deps = ", ".join(ticket["depends_on"]) or "None"
        lines.append(f"| {ticket['id']} | {ticket['epic']} | [{ticket['title']}](tickets/{ticket['id']}.md) | {ticket['stack']} | {deps} |")
    lines += ["", "## Epics", ""]
    for epic in backlog["epics"]:
        lines += [f"### [{epic['id']}: {epic['title']}](epics/{epic['id']}.md)", "",
            epic["outcome"], "", f"Exit: {epic['exit_criteria']}", ""]
    docs["docs/learning/roadmap.md"] = "\n".join(lines).rstrip() + "\n"
    owners = {cap: t["id"] for t in tickets for cap in t["provides_capabilities"]}
    lines = ["# Capability ownership", "",
        "The sequential ticket order is the default learning path. These additional gates show the earlier implementation capabilities a task actually consumes.",
        "A capability is available only after its owning ticket meets its acceptance criteria; the initial board does not claim any capability is already implemented.",
        "", "| Ticket | Requires implementation from | Produces |", "| --- | --- | --- |"]
    for ticket in tickets:
        needs = list(dict.fromkeys(owners[c] for c in ticket["requires_capabilities"]))
        links = ", ".join(f"[{key}](tickets/{key}.md)" for key in needs) or "None"
        provides = ", ".join(code(c) for c in ticket["provides_capabilities"])
        lines.append(f"| [{ticket['id']}](tickets/{ticket['id']}.md) | {links} | {provides} |")
    docs["docs/learning/capabilities.md"] = "\n".join(lines) + "\n"
    return docs


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    backlog = json.loads((ROOT / "docs/learning/backlog.json").read_text())
    registry = json.loads((ROOT / "docs/sources.json").read_text())
    mapping = json.loads((ROOT / "docs/learning/github-issues.json").read_text())
    stale = []
    for relative, expected in generated_documents(backlog, registry, mapping).items():
        path = ROOT / relative
        if args.check:
            if not path.is_file() or path.read_text() != expected:
                stale.append(relative)
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(expected)
    if stale:
        raise SystemExit("Generated documents are stale; run python3 scripts/render_learning.py:\n" + "\n".join(stale))
    print("Learning documents match their canonical sources." if args.check else "Rendered all learning documents.")


if __name__ == "__main__":
    main()
