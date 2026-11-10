"""Hermetic test settings: fake mode, development identity, and nothing read from the developer's environment."""

from __future__ import annotations

import os
import tempfile

import pytest

# Importing the app module builds a default host: point it at a throwaway file, never the developer's local database.
os.environ["STORAGE__PATH"] = os.path.join(tempfile.mkdtemp(prefix="support-import-"), "support.db")

ENVIRONMENT_PREFIXES = ("MODEL_PROVIDER__", "IDENTITY__", "APP_ENVIRONMENT", "ANTHROPIC_API_KEY")


@pytest.fixture(autouse=True)
def clean_environment(monkeypatch: pytest.MonkeyPatch, tmp_path: os.PathLike[str]) -> None:
    for name in list(os.environ):
        if name.upper().startswith(ENVIRONMENT_PREFIXES):
            monkeypatch.delenv(name, raising=False)
    # Every test gets its own database file (L38), also when it builds Settings itself (live and host tests).
    monkeypatch.setenv("STORAGE__PATH", os.path.join(tmp_path, "support.db"))
