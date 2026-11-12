"""Server configuration, read from the environment and validated at startup (fail fast, never at the first request).

Names mirror the .NET backend's sections with ``__`` as the separator, for example ``MODEL_PROVIDER__MODE`` or
``IDENTITY__DEVELOPMENT_CUSTOMER_ID``. Secrets come only from the environment (or Aspire parameters); nothing secret
is committed, logged, or returned by any endpoint.
"""

from __future__ import annotations

from enum import StrEnum
from pathlib import Path
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


class StorageSettings(BaseModel):
    # One SQLite file per backend and per run; relative paths are resolved from the working directory.
    path: Path = Path(".data") / "python" / "support.db"


class TelemetrySettings(BaseModel):
    # Off by default. When on, prompts, replies, and tool arguments/results go into spans: local debugging only.
    capture_sensitive_data: bool = False


class ResilienceSettings(BaseModel):
    """Every limit that bounds a turn (the same defaults as .NET's ``Resilience`` section). Out-of-range values
    refuse to start, so a deadline or budget can always be created. Timeouts are seconds and may be fractional."""

    # Wall-clock limit for one turn (or one resumed approval), from start to its result. Then: failed/timeout.
    turn_timeout_seconds: float = Field(default=90, gt=0, le=3600)
    # Model calls one turn may make (triage, specialist steps, declines, resume). Then: failed/budget_exhausted.
    max_model_calls_per_turn: int = Field(default=10, ge=1, le=100)
    # Tool-calling iterations within one agent run (MAF's function-invocation loop), then one final answer without tools.
    max_tool_iterations: int = Field(default=4, ge=1, le=50)
    # Time one tool call may take. Then: failed/tool_unavailable.
    tool_timeout_seconds: float = Field(default=10, gt=0, le=600)
    # The Anthropic SDK's own retries per model call: the only retry layer for model calls.
    model_max_retries: int = Field(default=2, ge=0, le=10)
    # Time one HTTP attempt to the model may take (the SDK retries a timed-out attempt).
    model_attempt_timeout_seconds: float = Field(default=30, gt=0, le=600)
    # Output cap per model call.
    max_output_tokens: int = Field(default=1024, ge=1, le=64000)


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
    storage: StorageSettings = Field(default_factory=StorageSettings)
    telemetry: TelemetrySettings = Field(default_factory=TelemetrySettings)
    resilience: ResilienceSettings = Field(default_factory=ResilienceSettings)
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
