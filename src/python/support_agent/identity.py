"""Who the current customer is. Only the server decides.

The application asks :class:`PrincipalSource` and nothing else: headers, query strings, request bodies, and model
output are never consulted for identity. In development the principal is one fixed customer from server
configuration; anywhere else there is no identity provider yet, and resolving a principal fails closed. Tests replace
the source through FastAPI's dependency overrides, a server-side injection point no client can reach. This is not
authentication.
"""

from __future__ import annotations

from collections.abc import Generator
from contextlib import contextmanager
from contextvars import ContextVar
from dataclasses import dataclass
from typing import Protocol

from support_agent.settings import Environment, Settings


@dataclass(frozen=True, slots=True)
class Principal:
    customer_id: str


class PrincipalSource(Protocol):
    def current(self) -> Principal: ...


class DevelopmentPrincipalSource:
    """Always the configured development customer."""

    def __init__(self, customer_id: str) -> None:
        self._principal = Principal(customer_id)

    def current(self) -> Principal:
        return self._principal


class NoIdentityProvider:
    """Outside development: there is no identity provider until production authentication exists. Fail closed."""

    def current(self) -> Principal:
        raise RuntimeError("No identity provider is configured outside development (fail closed).")


def principal_source_for(settings: Settings) -> PrincipalSource:
    if settings.app_environment is Environment.DEVELOPMENT and settings.identity.development_customer_id:
        return DevelopmentPrincipalSource(settings.identity.development_customer_id)
    return NoIdentityProvider()


_pinned: ContextVar[Principal | None] = ContextVar("pinned_principal", default=None)


@contextmanager
def pinned(principal: Principal) -> Generator[Principal]:
    """Pins the principal for one turn's work. Tools read it with :func:`current_principal`, never from arguments."""
    token = _pinned.set(principal)
    try:
        yield principal
    finally:
        _pinned.reset(token)


_process_principal: Principal | None = None


def set_process_principal(principal: Principal | None) -> None:
    """For the development-only DevUI process, which serves one configured customer: the principal its runs use.

    The API never sets this: there, every turn pins the caller's principal and anything unpinned fails closed.
    """
    global _process_principal  # deliberately process-wide, set once at DevUI startup
    _process_principal = principal


def current_principal() -> Principal:
    """The principal the current work runs as: the pinned one, else (DevUI only) the process principal; else fail closed."""
    principal = _pinned.get() or _process_principal
    if principal is None:
        raise RuntimeError("No principal is pinned for this work (fail closed).")
    return principal
