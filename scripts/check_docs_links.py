#!/usr/bin/env python3
"""Check every relative link, anchor, and image in the repository's Markdown (L34).

The documentation is plain GitHub-rendered Markdown, so a broken relative link only shows up when someone clicks
it. This checker reads the Markdown files Git tracks (generated, ignored output is never checked), finds inline
links and images, reference definitions, and HTML ``href``/``src`` attributes outside code, and fails if a target
file or folder is missing or a ``#anchor`` doesn't match a heading (GitHub's slug rules) or an explicit HTML anchor.

It also checks that every page under ``docs/`` can be reached from the index, ``docs/README.md``, by following
relative links: a page nobody links to is a page nobody reads.

External links (``https:``, ``mailto:``, ...) are not fetched: CI stays offline and deterministic.
Standard library only.

    python3 scripts/check_docs_links.py            # check the repository
    python3 scripts/check_docs_links.py ROOT       # check another tree (used by the tests)
"""

from __future__ import annotations

import re
import subprocess
import sys
import unicodedata
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import unquote

FENCE = re.compile(r"^ {0,3}(`{3,}|~{3,})")
INLINE_CODE = re.compile(r"(`+)(?:(?!\1).)+?\1")
# [text](target) and ![alt](target); the target may be <wrapped> and may carry a "title".
# The destination may hold balanced parentheses, as Markdown allows: guide(v2).md.
INLINE_LINK = re.compile(r"!?\[(?:[^\[\]]|\[[^\[\]]*\])*\]\(\s*(<[^>]*>|(?:[^()\s]|\([^()\s]*\))+)(?:\s+(?:\"[^\"]*\"|'[^']*'|\([^)]*\)))?\s*\)")
REFERENCE_DEFINITION = re.compile(r"^ {0,3}\[[^\]]+\]:\s*(<[^>]*>|\S+)")
HTML_ATTRIBUTE = re.compile(r"""\b(?:href|src)\s*=\s*["']([^"']+)["']""", re.IGNORECASE)
HTML_ANCHOR = re.compile(r"""<a\s[^>]*\b(?:id|name)\s*=\s*["']([^"']+)["']""", re.IGNORECASE)
BLOCKQUOTE = re.compile(r"^ {0,3}(?:> ?)+")
LIST_ITEM = re.compile(r"^ {0,3}(?:[-*+]|\d{1,9}[.)])(?:\s|$)")
INDENTED = re.compile(r"^(?: {4}|\t)")
SETEXT_UNDERLINE = re.compile(r"^ {0,3}(?:=+|-+)\s*$")
HEADING = re.compile(r"^ {0,3}(#{1,6})\s+(.*?)\s*#*\s*$")
SCHEME = re.compile(r"^[a-zA-Z][a-zA-Z0-9+.-]*:")


@dataclass(frozen=True)
class Problem:
    file: Path
    line: int
    target: str
    reason: str

    def __str__(self) -> str:
        return f"{self.file}:{self.line}: {self.target} -> {self.reason}"


HTML_COMMENT = re.compile(r"<!--.*?-->", re.DOTALL)


def without_comments(text: str) -> str:
    """HTML comments are not rendered, so links in them aren't links. Line breaks are kept so line numbers hold."""
    return HTML_COMMENT.sub(lambda m: "\n" * m.group(0).count("\n"), text)


def prose_lines(text: str, keep_inline_code: bool = False) -> list[tuple[int, str]]:
    """The lines outside code: fenced blocks, and indented blocks (four spaces or a tab, after a blank line, outside a
    list, where indentation continues the list item instead). Inline code is removed unless kept: links in code are
    examples, not links, but a heading's code text is part of its anchor."""
    lines: list[tuple[int, str]] = []
    fence: str | None = None
    previous_blank = True  # the start of a document counts as after a blank line
    in_list = False
    in_indented_code = False
    for number, line in enumerate(without_comments(text).splitlines(), start=1):
        blank = not line.strip()
        # Fences may sit inside a container: a blockquote (">" prefixes) or a list item (indented to its content).
        inner = BLOCKQUOTE.sub("", line)
        fence_line = inner.lstrip(" ") if in_list else inner
        if fence is None and not FENCE.match(fence_line):
            if in_indented_code and (blank or INDENTED.match(line)):
                previous_blank = blank
                continue  # an indented block runs until a non-blank line that isn't indented
            in_indented_code = False
            if not blank and INDENTED.match(line) and previous_blank and not in_list:
                in_indented_code = True
                previous_blank = False
                continue
            if LIST_ITEM.match(line):
                in_list = True
            elif not blank and not INDENTED.match(line):
                in_list = False
            previous_blank = blank
        match = FENCE.match(fence_line)
        if fence is None and match:
            fence = match.group(1)  # the whole opening run: ```` can wrap a ``` example
            continue
        if fence is not None:
            # CommonMark: closed only by the same character, at least as many, and nothing else on the line.
            closing = re.match(rf"^ {{0,3}}({re.escape(fence[0])}{{{len(fence)},}})\s*$", fence_line)
            if closing:
                fence = None
            continue
        lines.append((number, line if keep_inline_code else INLINE_CODE.sub("", line)))
    return lines


def slug(heading: str) -> str:
    """GitHub's heading anchor: plain text, lower case, punctuation dropped, each space a hyphen."""
    text = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", heading)  # links and images keep their text
    text = re.sub(r"<[^>]+>", "", text)  # inline HTML
    text = text.replace("`", "").lower()
    kept = "".join(c for c in text if c in " -_" or unicodedata.category(c)[0] in "LN")
    return kept.replace(" ", "-")


def anchors(text: str) -> set[str]:
    """Every anchor a page offers: heading slugs (duplicates get -1, -2, ...) and explicit HTML ids and names."""
    found: set[str] = set()
    seen: dict[str, int] = {}

    def add(heading: str) -> None:
        base = slug(heading)
        count = seen.get(base, 0)
        seen[base] = count + 1
        found.add(base if count == 0 else f"{base}-{count}")

    previous: tuple[int, str] | None = None  # the line before, if it was a plain paragraph line
    for number, line in prose_lines(text, keep_inline_code=True):
        heading = HEADING.match(line)
        if heading:
            add(heading.group(2))
        elif SETEXT_UNDERLINE.match(line) and previous is not None and previous[0] == number - 1:
            add(previous[1].strip())  # Setext: a paragraph line underlined with === or ---
        found.update(name.lower() for name in HTML_ANCHOR.findall(line))
        plain = line.strip() and not heading and not SETEXT_UNDERLINE.match(line) and not LIST_ITEM.match(line) and not BLOCKQUOTE.match(line)
        previous = (number, line) if plain and not line.lstrip().startswith("|") else None
    return found


def targets(line: str) -> list[str]:
    links = [m.group(1) for m in INLINE_LINK.finditer(line)]
    definition = REFERENCE_DEFINITION.match(line)
    if definition:
        links.append(definition.group(1))
    links.extend(HTML_ATTRIBUTE.findall(line))
    return [link[1:-1] if link.startswith("<") and link.endswith(">") else link for link in links]


def check_file(root: Path, file: Path, cache: dict[Path, set[str]]) -> list[Problem]:
    problems: list[Problem] = []
    text = file.read_text(encoding="utf-8")
    for number, line in prose_lines(text):
        for target in targets(line):
            if not target or SCHEME.match(target) or target.startswith("//"):
                continue  # external: not fetched
            path_part, _, anchor = target.partition("#")
            path_part = unquote(path_part.split("?", 1)[0])
            if path_part:
                resolved = (root / path_part.lstrip("/")) if path_part.startswith("/") else (file.parent / path_part)
                resolved = resolved.resolve()
                if not resolved.is_relative_to(root.resolve()):
                    problems.append(Problem(file.relative_to(root), number, target, "points outside the repository"))
                    continue
                if not resolved.exists():
                    problems.append(Problem(file.relative_to(root), number, target, "no such file or folder"))
                    continue
            else:
                resolved = file.resolve()
            if anchor and resolved.is_file() and resolved.suffix.lower() == ".md":
                if resolved not in cache:
                    cache[resolved] = anchors(resolved.read_text(encoding="utf-8"))
                if unquote(anchor).lower() not in cache[resolved]:
                    problems.append(Problem(file.relative_to(root), number, target, f"no heading or anchor '#{anchor}'"))
    return problems


def markdown_files(root: Path) -> list[Path]:
    """The tracked Markdown files (Git's view, so ignored and generated output is never checked)."""
    try:
        listed = subprocess.run(
            ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard", "*.md"],
            cwd=root,
            check=True,
            capture_output=True,
        ).stdout.decode("utf-8")
        return sorted(root / name for name in listed.split("\0") if name and (root / name).is_file())
    except (OSError, subprocess.CalledProcessError):
        return sorted(p for p in root.rglob("*.md") if ".git" not in p.parts)


def linked_pages(root: Path, file: Path) -> set[Path]:
    """The existing Markdown files a page links to (relative links only)."""
    pages: set[Path] = set()
    for _, line in prose_lines(file.read_text(encoding="utf-8")):
        for target in targets(line):
            if not target or SCHEME.match(target) or target.startswith("//"):
                continue
            path_part = unquote(target.partition("#")[0].split("?", 1)[0])
            if not path_part:
                continue
            resolved = ((root / path_part.lstrip("/")) if path_part.startswith("/") else (file.parent / path_part)).resolve()
            if resolved.is_file() and resolved.suffix.lower() == ".md":
                pages.add(resolved)
    return pages


def unreachable(root: Path, index: str = "docs/README.md") -> list[Path]:
    """The pages under docs/ that no chain of relative links from the index reaches (empty without an index)."""
    start = (root / index).resolve()
    if not start.is_file():
        return []
    seen = {start}
    queue = [start]
    while queue:
        for page in linked_pages(root, queue.pop()):
            if page not in seen:
                seen.add(page)
                queue.append(page)
    docs = (root / "docs").resolve()
    return [p.relative_to(root) for p in markdown_files(root) if p.resolve().is_relative_to(docs) and p.resolve() not in seen]


def check(root: Path) -> list[Problem]:
    cache: dict[Path, set[str]] = {}
    problems: list[Problem] = []
    for file in markdown_files(root):
        problems.extend(check_file(root, file, cache))
    return problems


def main(argv: list[str]) -> int:
    root = Path(argv[1]) if len(argv) > 1 else Path(__file__).resolve().parents[1]
    files = markdown_files(root)
    problems = check(root)
    for problem in problems:
        print(problem, file=sys.stderr)
    orphans = unreachable(root)
    for orphan in orphans:
        print(f"{orphan}: not reachable from docs/README.md", file=sys.stderr)
    print(f"Checked {len(files)} Markdown files: {len(problems)} broken links, {len(orphans)} unreachable pages.")
    return 1 if problems or orphans else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
