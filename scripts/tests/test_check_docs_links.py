"""The documentation link checker: it fails on a broken relative link, anchor, or image, and only on those."""

from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import check_docs_links  # noqa: E402


class CheckDocsLinksTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        (self.root / "docs").mkdir()
        (self.root / "docs" / "img.png").write_bytes(b"\x89PNG")
        (self.root / "src").mkdir()
        self.write(
            "docs/guide.md",
            "# Guide\n\n## Run it (locally)\n\n## Setup\n\n## Setup\n\n<a id=\"custom-anchor\"></a>\n\n### `report` command\n",
        )

    def tearDown(self) -> None:
        self.directory.cleanup()

    def write(self, name: str, text: str) -> None:
        (self.root / name).write_text(text, encoding="utf-8")

    def problems(self) -> list[str]:
        return [f"{p.target} -> {p.reason}" for p in check_docs_links.check(self.root)]

    def test_valid_links_anchors_images_and_folders_pass(self) -> None:
        self.write(
            "README.md",
            "[guide](docs/guide.md) [run](docs/guide.md#run-it-locally) [second](docs/guide.md#setup-1)\n"
            "[custom](docs/guide.md#custom-anchor) [code heading](docs/guide.md#report-command)\n"
            "![img](docs/img.png) [folder](src/) [self](#top) [ext](https://example.com/missing) [mail](mailto:a@b.c)\n\n"
            "# Top\n\n[ref]: docs/guide.md#setup\n",
        )
        self.assertEqual(self.problems(), [])

    def test_a_missing_file_fails(self) -> None:
        self.write("README.md", "See [the runbook](docs/runbook.md).\n")
        self.assertEqual(self.problems(), ["docs/runbook.md -> no such file or folder"])

    def test_a_missing_anchor_fails(self) -> None:
        self.write("README.md", "See [setup](docs/guide.md#installation).\n")
        self.assertEqual(self.problems(), ["docs/guide.md#installation -> no heading or anchor '#installation'"])

    def test_a_missing_anchor_in_the_same_page_fails(self) -> None:
        self.write("README.md", "# Title\n\nJump to [usage](#usage).\n")
        self.assertEqual(self.problems(), ["#usage -> no heading or anchor '#usage'"])

    def test_a_missing_image_fails(self) -> None:
        self.write("README.md", "![diagram](docs/diagram.png)\n<img src=\"docs/other.svg\">\n")
        self.assertEqual(
            self.problems(),
            ["docs/diagram.png -> no such file or folder", "docs/other.svg -> no such file or folder"],
        )

    def test_a_broken_reference_definition_fails(self) -> None:
        self.write("README.md", "[guide][g]\n\n[g]: docs/gone.md\n")
        self.assertEqual(self.problems(), ["docs/gone.md -> no such file or folder"])

    def test_links_in_code_are_examples_not_links(self) -> None:
        self.write("README.md", "```markdown\n[x](missing.md)\n```\n\nInline `[y](also-missing.md)` too.\n")
        self.assertEqual(self.problems(), [])

    def test_a_longer_fence_wraps_a_shorter_one(self) -> None:
        self.write(
            "README.md",
            "````markdown\n```text\n[example](not-a-real-file.md)\n```\n````\n\n"
            "~~~\n[also](missing-too.md)\n```\n~~~\n\n[real](docs/gone.md)\n",
        )
        self.assertEqual(self.problems(), ["docs/gone.md -> no such file or folder"])

    def test_an_indented_code_block_is_code_but_an_indented_list_paragraph_is_prose(self) -> None:
        self.write(
            "README.md",
            "Example:\n\n    [example](not-a-real-file.md)\n\n    still code [x](nope.md)\n\nBack to prose.\n\n"
            "- A list item\n\n    [continued](docs/gone.md)\n",
        )
        self.assertEqual(self.problems(), ["docs/gone.md -> no such file or folder"])

    def test_fences_inside_blockquotes_and_list_items_are_code(self) -> None:
        self.write(
            "README.md",
            "> ```markdown\n> [example](not-a-real-file.md)\n> ```\n\n"
            "1. Step one:\n\n    ```text\n    [also](missing.md)\n    ```\n\n"
            "2. Then see [real](docs/gone.md).\n\n> Quoted [link](docs/gone-too.md).\n",
        )
        self.assertEqual(self.problems(), ["docs/gone.md -> no such file or folder", "docs/gone-too.md -> no such file or folder"])

    def test_balanced_parentheses_in_a_destination(self) -> None:
        (self.root / "docs" / "guide(v2).md").write_text("# Guide v2\n", encoding="utf-8")
        self.write("README.md", "[v2](docs/guide(v2).md) [v2 anchor](docs/guide(v2).md#guide-v2) [gone](docs/gone(v3).md)\n")
        self.assertEqual(self.problems(), ["docs/gone(v3).md -> no such file or folder"])

    def test_a_link_out_of_the_repository_fails(self) -> None:
        self.write("README.md", "[up](../../etc/passwd)\n")
        self.assertEqual(self.problems(), ["../../etc/passwd -> points outside the repository"])

    def test_setext_headings_are_anchors_but_a_rule_after_a_blank_line_is_not(self) -> None:
        self.write("docs/setext.md", "Installation\n============\n\nNext steps\n----------\n\nText.\n\n---\n")
        self.write("README.md", "[a](docs/setext.md#installation) [b](docs/setext.md#next-steps) [c](docs/setext.md#text)\n")
        self.assertEqual(self.problems(), ["docs/setext.md#text -> no heading or anchor '#text'"])

    def test_links_in_html_comments_are_not_links_and_line_numbers_hold(self) -> None:
        self.write("README.md", "<!-- [hidden](gone.md)\n[also hidden](gone-too.md) -->\n\n[real](docs/missing.md)\n")
        problems = check_docs_links.check(self.root)
        self.assertEqual([(p.target, p.line) for p in problems], [("docs/missing.md", 4)])

    def test_github_slugs(self) -> None:
        self.assertEqual(check_docs_links.slug("Contract API (version 1)"), "contract-api-version-1")
        self.assertEqual(check_docs_links.slug("0003 - Durable state"), "0003---durable-state")
        self.assertEqual(check_docs_links.slug("Live (opt-in)"), "live-opt-in")
        self.assertEqual(check_docs_links.slug("[Linked](x.md) `code` *em*"), "linked-code-em")
        self.assertEqual(check_docs_links.slug("snake_case_name"), "snake_case_name")

    def test_every_docs_page_must_be_reachable_from_the_index(self) -> None:
        self.write("docs/README.md", "# Docs\n\n[guide](guide.md)\n")
        self.write("docs/orphan.md", "# Nobody links here\n")
        (self.root / "docs" / "sub").mkdir()
        self.write("docs/sub/deep.md", "# Deep\n")
        self.write("docs/guide.md", "# Guide\n\n[deep](sub/deep.md)\n")  # reachable through guide.md
        self.assertEqual(check_docs_links.unreachable(self.root), [Path("docs/orphan.md")])
        self.assertEqual(check_docs_links.main(["check_docs_links.py", str(self.root)]), 1)
        self.write("docs/README.md", "# Docs\n\n[guide](guide.md) [orphan](orphan.md)\n")
        self.assertEqual(check_docs_links.unreachable(self.root), [])

    def test_the_command_exits_non_zero_on_a_broken_link(self) -> None:
        self.write("README.md", "[gone](docs/gone.md)\n")
        self.assertEqual(check_docs_links.main(["check_docs_links.py", str(self.root)]), 1)
        self.write("README.md", "[ok](docs/guide.md)\n")
        self.assertEqual(check_docs_links.main(["check_docs_links.py", str(self.root)]), 0)


if __name__ == "__main__":
    unittest.main()
