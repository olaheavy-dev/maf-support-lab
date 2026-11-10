"""FastAPI dependencies: the only way a request reaches settings and identity."""

from __future__ import annotations

from typing import Annotated

from fastapi import Depends, Request

from support_agent.identity import Principal, PrincipalSource
from support_agent.settings import Settings


def get_settings(request: Request) -> Settings:
    settings: Settings = request.app.state.settings
    return settings


def get_principal_source(request: Request) -> PrincipalSource:
    source: PrincipalSource = request.app.state.principal_source
    return source


def get_principal(source: Annotated[PrincipalSource, Depends(get_principal_source)]) -> Principal:
    """The only way a request learns who the customer is. Tests override get_principal_source (server side)."""
    return source.current()


Caller = Annotated[Principal, Depends(get_principal)]
