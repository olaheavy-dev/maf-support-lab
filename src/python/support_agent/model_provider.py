"""The provider boundary: the fake model in fake mode, Claude in live mode (L18).

The live client is created once by the host and owned by its lifespan (closed at shutdown). Creating it makes no call.
"""

from __future__ import annotations

from typing import Any

from agent_framework import SupportsChatGetResponse
from agent_framework.anthropic import AnthropicClient

from support_agent.fake_model import FakeChatClient
from support_agent.settings import ModelMode, Settings


def create_client(settings: Settings) -> SupportsChatGetResponse[Any]:
    if settings.model_provider.mode is ModelMode.FAKE:
        return FakeChatClient()
    anthropic = settings.model_provider.anthropic
    key = anthropic.api_key.get_secret_value() if anthropic.api_key else None
    return AnthropicClient(api_key=key, model=anthropic.model)
