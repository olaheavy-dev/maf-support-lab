"""The ASGI application: health, the development whoami endpoint, and the shared host shell.

Run it with ``uv run python -m support_agent`` (see README.md), or through the Aspire AppHost, which starts it with
Uvicorn as ``support_agent.app:app``. Agents and the contract API arrive in later tickets (L18+).
"""

from __future__ import annotations

from collections.abc import AsyncGenerator, Awaitable, Callable
from contextlib import asynccontextmanager
from typing import Annotated, cast

from fastapi import Depends, FastAPI
from fastapi.responses import PlainTextResponse

from support_agent.agents import create_agents
from support_agent.api import create_router
from support_agent.conversations import ConversationStore
from support_agent.database import Database
from support_agent.dependencies import get_principal, get_principal_source, get_settings
from support_agent.fixtures import Fixtures
from support_agent.identity import Principal, principal_source_for
from support_agent.model_provider import create_client
from support_agent.operations import build_operations
from support_agent.resources import Resources
from support_agent.services import Services
from support_agent.settings import Environment, Settings


def create_app(settings: Settings | None = None) -> FastAPI:
    """Builds the host. Settings are validated before this returns, so bad configuration never starts serving."""
    settings = settings or Settings()
    resources = Resources()

    @asynccontextmanager
    async def lifespan(_: FastAPI) -> AsyncGenerator[None]:
        yield
        await resources.close_all()

    app = FastAPI(
        title="VoltCart support API (Python)", lifespan=lifespan, docs_url=None, redoc_url=None, openapi_url=None
    )
    app.state.settings = settings
    app.state.principal_source = principal_source_for(settings)
    app.state.resources = resources

    # One model client per process, owned by the lifespan; the agents and the guarded services are built once.
    client = create_client(settings)
    resources.own("model client", _closer(client))
    # One SQLite file per backend and run (L38), opened at startup and closed (checkpointed) at shutdown.
    database = Database(settings.storage.path)
    resources.own("database", _closing(database))
    app.state.database = database
    app.state.services = Services.create(Fixtures.load(), database)
    app.state.operations = build_operations(app.state.services.state, database)
    app.state.agents = create_agents(client, app.state.services, app.state.operations)
    app.state.conversations = ConversationStore(database)
    app.include_router(create_router(app.state.conversations, app.state.agents, app.state.operations))

    @app.get("/health", response_class=PlainTextResponse)
    def health(current: Annotated[Settings, Depends(get_settings)]) -> str:
        # Configuration was validated at startup; health never calls the model provider (no billable calls).
        _ = current.model_provider.mode
        return "Healthy"

    @app.get("/alive", response_class=PlainTextResponse)
    def alive() -> str:
        return "Healthy"

    if settings.app_environment is Environment.DEVELOPMENT:

        @app.get("/dev/whoami")
        def whoami(principal: Annotated[Principal, Depends(get_principal)]) -> dict[str, str]:
            return {"customer_id": principal.customer_id}

    return app


def _closer(client: object) -> Callable[[], Awaitable[None]]:
    async def close() -> None:
        closing = getattr(client, "close", None)
        if callable(closing):
            outcome = closing()
            if hasattr(outcome, "__await__"):
                await cast(Awaitable[None], outcome)

    return close


def _closing(database: Database) -> Callable[[], Awaitable[None]]:
    async def close() -> None:
        database.close()

    return close


def _create_default_app() -> FastAPI:
    return create_app()


app = _create_default_app()


__all__ = ["app", "create_app", "get_principal", "get_principal_source", "get_settings"]
