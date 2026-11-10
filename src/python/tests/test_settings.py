"""L17: configuration is validated at startup.

Fake mode needs no credentials; live mode refuses to start without them.
"""

from __future__ import annotations

import traceback

import pytest
from pydantic import ValidationError

from support_agent.settings import Environment, ModelMode, Settings


def test_fake_mode_starts_without_any_credentials(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("APP_ENVIRONMENT", "development")
    monkeypatch.setenv("IDENTITY__DEVELOPMENT_CUSTOMER_ID", "CUST-1001")

    settings = Settings()

    assert settings.model_provider.mode is ModelMode.FAKE
    assert settings.model_provider.anthropic.api_key is None


@pytest.mark.parametrize(
    ("variables", "message"),
    [
        ({"MODEL_PROVIDER__MODE": "live", "ANTHROPIC_API_KEY": "sk-test"}, "MODEL_PROVIDER__ANTHROPIC__MODEL"),
        ({"MODEL_PROVIDER__MODE": "live", "MODEL_PROVIDER__ANTHROPIC__MODEL": "claude-haiku-5-5"}, "API key"),
        (
            {
                "MODEL_PROVIDER__MODE": "live",
                "MODEL_PROVIDER__ANTHROPIC__MODEL": "claude-haiku-5-5",
                "ANTHROPIC_API_KEY": "  ",
            },
            "API key",
        ),
        ({"MODEL_PROVIDER__MODE": "sometimes"}, "mode"),
        ({"APP_ENVIRONMENT": "development"}, "IDENTITY__DEVELOPMENT_CUSTOMER_ID"),
    ],
)
def test_invalid_configuration_is_refused_at_startup(
    monkeypatch: pytest.MonkeyPatch, variables: dict[str, str], message: str
) -> None:
    for name, value in variables.items():
        monkeypatch.setenv(name, value)

    with pytest.raises(ValidationError, match=message):
        Settings()


def test_live_mode_reads_the_standard_key_variable_and_never_shows_it(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("MODEL_PROVIDER__MODE", "live")
    monkeypatch.setenv("MODEL_PROVIDER__ANTHROPIC__MODEL", "claude-haiku-5-5")
    monkeypatch.setenv("ANTHROPIC_API_KEY", "sk-test-secret")

    settings = Settings()

    key = settings.model_provider.anthropic.api_key
    assert key is not None
    assert key.get_secret_value() == "sk-test-secret"
    assert "sk-test-secret" not in repr(settings)
    assert "sk-test-secret" not in settings.model_dump_json()


def test_the_default_environment_is_production() -> None:
    assert Settings().app_environment is Environment.PRODUCTION


def test_a_configuration_error_never_prints_the_api_key(monkeypatch: pytest.MonkeyPatch) -> None:
    # Live mode with a key but no model fails at startup; the error must not echo the key from the environment.
    monkeypatch.setenv("MODEL_PROVIDER__MODE", "live")
    monkeypatch.setenv("ANTHROPIC_API_KEY", "sk-test-must-not-leak")

    with pytest.raises(ValidationError) as error:
        Settings()

    # What reaches logs and the console is the formatted exception: it must not contain the key.
    printed = "".join(traceback.format_exception(error.value))
    assert "sk-test-must-not-leak" not in printed
