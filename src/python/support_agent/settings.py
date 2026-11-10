"""Server configuration, read from the environment and validated at startup (fail fast, never at the first request).

Names mirror the .NET backend's sections with ``__`` as the separator, for example ``MODEL_PROVIDER__MODE`` or
``IDENTITY__DEVELOPMENT_CUSTOMER_ID``. Secrets come only from the environment (or Aspire parameters); nothing secret
is committed, logged, or returned by any endpoint.
"""

from __future__ import annotations

from enum import StrEnum
from typing import Self

from pydantic import BaseModel, Field, SecretStr, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


class Environment(StrEnum):
    DEVELOPMENT = "development"
    PRODUCTION = "production"


class ModelMode(StrEnum):
    FAKE = "fake"
    LIVE = "live"


class AnthropicSettings(BaseModel):
    model: str | None = None
    api_key: SecretStr | None = None


class ModelProviderSettings(BaseModel):
    mode: ModelMode = ModelMode.FAKE
    anthropic: AnthropicSettings = Field(default_factory=AnthropicSettings)


class IdentitySettings(BaseModel):
    development_customer_id: str | None = None


class Settings(BaseSettings):
    """Everything the host needs. Fake mode needs no credentials; live mode is validated here, before serving."""

    # hide_input_in_errors: a validation error must never echo the raw environment, which may hold the API key.
    model_config = SettingsConfigDict(
        env_nested_delimiter="__", case_sensitive=False, extra="ignore", hide_input_in_errors=True
    )

    app_environment: Environment = Environment.PRODUCTION
    model_provider: ModelProviderSettings = Field(default_factory=ModelProviderSettings)
    identity: IdentitySettings = Field(default_factory=IdentitySettings)
    # The standard Anthropic variable, used when MODEL_PROVIDER__ANTHROPIC__API_KEY is not set (as in .NET).
    anthropic_api_key: SecretStr | None = None

    @model_validator(mode="after")
    def _validate(self) -> Self:
        if self.model_provider.anthropic.api_key is None and self.anthropic_api_key is not None:
            self.model_provider.anthropic.api_key = self.anthropic_api_key

        if self.model_provider.mode is ModelMode.LIVE:
            anthropic = self.model_provider.anthropic
            if not anthropic.model:
                raise ValueError("MODEL_PROVIDER__ANTHROPIC__MODEL is required in live mode.")
            if anthropic.api_key is None or not anthropic.api_key.get_secret_value().strip():
                raise ValueError("An Anthropic API key is required in live mode (ANTHROPIC_API_KEY).")

        if (
            self.app_environment is Environment.DEVELOPMENT
            and not (self.identity.development_customer_id or "").strip()
        ):
            raise ValueError("IDENTITY__DEVELOPMENT_CUSTOMER_ID must be set in development.")
        return self
