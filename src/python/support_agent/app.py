"""The ASGI application: health, the development whoami endpoint, and the shared host shell.

Run it with ``uv run python -m support_agent`` (see README.md), or through the Aspire AppHost, which starts it with
Uvicorn as ``support_agent.app:app``. Agents and the contract API arrive in later tickets (L18+).
"""

from __future__ import annotations

from collections.abc import AsyncGenerator
from contextlib import asynccontextmanager
from typing import Annotated

from fastapi import Depends, FastAPI, Request
from fastapi.responses import PlainTextResponse

from support_agent.identity import Principal, PrincipalSource, principal_source_for
from support_agent.resources import Resources
from support_agent.settings import Environment, Settings


def get_settings(request: Request) -> Settings:
    settings: Settings = request.app.state.settings
    return settings


def get_principal_source(request: Request) -> PrincipalSource:
    source: PrincipalSource = request.app.state.principal_source
    return source


def get_principal(source: Annotated[PrincipalSource, Depends(get_principal_source)]) -> Principal:
    """The only way a request learns who the customer is."""
    return source.current()


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


def _create_default_app() -> FastAPI:
    return create_app()


app = _create_default_app()
