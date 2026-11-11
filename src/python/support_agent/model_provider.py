"""The provider boundary: the fake model in fake mode, Claude in live mode (L18; bounded since L42).

The live client is created once by the host and owned by its lifespan (closed at shutdown). Creating it makes no call.
The Anthropic SDK client is built here, not by MAF, so its retry policy is ours and explicit (L42): it is the single
retry owner for model calls, with a time limit per HTTP attempt, and an SDK middleware counts every attempt per turn.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

import anthropic
from agent_framework import SupportsChatGetResponse
from agent_framework.anthropic import AnthropicClient

from support_agent.fake_model import FakeChatClient
from support_agent.resilience import count_attempts
from support_agent.settings import ModelMode, Settings


@dataclass
class ModelProvider:
    """The chat client the agents use, and what must be closed with it."""

    client: SupportsChatGetResponse[Any]
    sdk: anthropic.AsyncAnthropic | None = field(default=None)

    async def close(self) -> None:
        if self.sdk is not None:
            await self.sdk.close()  # MAF leaves a client it was given open, so its owner closes it


def create_provider(settings: Settings, http_client: anthropic.DefaultAsyncHttpxClient | None = None) -> ModelProvider:
    """``http_client`` is a server-side test seam (like .NET's keyed handler): tests pass a fake Anthropic HTTP API so the
    real SDK's retries run deterministically. Production passes nothing and the SDK uses its own client."""
    if settings.model_provider.mode is ModelMode.FAKE:
        return ModelProvider(FakeChatClient())
    anthropic_settings = settings.model_provider.anthropic
    limits = settings.resilience
    sdk = anthropic.AsyncAnthropic(
        api_key=anthropic_settings.api_key.get_secret_value() if anthropic_settings.api_key else None,
        max_retries=limits.model_max_retries,
        timeout=limits.model_attempt_timeout_seconds,
        http_client=http_client,
        middleware=[count_attempts],
    )
    return ModelProvider(AnthropicClient(model=anthropic_settings.model, anthropic_client=sdk), sdk)


def create_client(settings: Settings) -> SupportsChatGetResponse[Any]:
    """The chat client alone (DevUI's process lives as long as its client)."""
    return create_provider(settings).client
