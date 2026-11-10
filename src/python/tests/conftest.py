"""Hermetic test settings: fake mode, development identity, and nothing read from the developer's environment."""

from __future__ import annotations

import pytest

ENVIRONMENT_PREFIXES = ("MODEL_PROVIDER__", "IDENTITY__", "APP_ENVIRONMENT", "ANTHROPIC_API_KEY")


@pytest.fixture(autouse=True)
def clean_environment(monkeypatch: pytest.MonkeyPatch) -> None:
    import os

    for name in list(os.environ):
        if name.upper().startswith(ENVIRONMENT_PREFIXES):
            monkeypatch.delenv(name, raising=False)
