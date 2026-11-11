"""A diagnostic prompt-injection heuristic (L41, porting L14's InjectionSignals).

It looks for well-known attack phrasings in customer text and tool results and records which ones it found, on the
current span and as a metric, so attacks can be studied. **It never blocks, rewrites, or decides anything.** It has
false positives ("please ignore my previous message") and any paraphrase bypasses it. What keeps customers safe is
enforced in code whatever the text says: the pinned identity, ownership, eligibility, exact-argument approval, and
tool-argument validation. Only signal names are recorded, never the text.
"""

from __future__ import annotations

import re

from opentelemetry import trace

from support_agent.telemetry import meter

SIGNALS: tuple[tuple[str, re.Pattern[str]], ...] = (
    (
        "ignore_instructions",
        re.compile(
            r"\b(ignore|disregard|forget)\b.{0,30}\b(previous|prior|above|earlier|all|your|the)\b.{0,20}"
            r"\b(instructions?|rules|guidelines|prompts?|messages?)\b",
            re.IGNORECASE | re.DOTALL,
        ),
    ),
    (
        "role_override",
        re.compile(
            r"\b(you are now|act as|pretend to be|you're now)\b.{0,30}"
            r"\b(admin|administrator|root|developer|system|unrestricted|dan)\b",
            re.IGNORECASE | re.DOTALL,
        ),
    ),
    (
        "prompt_extraction",
        re.compile(
            r"\b(system prompt|your instructions|developer message|reveal your (rules|prompt))\b", re.IGNORECASE
        ),
    ),
    ("fake_system_message", re.compile(r"(^|\n)\s*(\[?system\]?|###\s*system|<\s*system\s*>)\s*[:>\]]", re.IGNORECASE)),
    (
        "skip_approval",
        re.compile(
            r"\b(without|skip|no need for|don't ask for|bypass)\b.{0,20}\b(approval|confirmation|permission|asking)\b",
            re.IGNORECASE | re.DOTALL,
        ),
    ),
)

signals_seen = meter.create_counter(
    "support.injection.signals",
    unit="{signal}",
    description="Prompt-injection phrasings seen (diagnostic only; never used to decide anything).",
)


def find(text: str | None) -> list[str]:
    """The names of the signals found in ``text`` (empty when none)."""
    if not text:
        return []
    return [name for name, pattern in SIGNALS if pattern.search(text)]


def inspect(source: str, text: str | None) -> list[str]:
    """Finds signals and records their names on the current span and the metric. Never changes what happens next."""
    found = find(text)
    if found:
        trace.get_current_span().set_attribute(f"support.injection.{source}", ",".join(found))
        for name in found:
            signals_seen.add(1, {"support.injection.source": source, "support.injection.signal": name})
    return found
