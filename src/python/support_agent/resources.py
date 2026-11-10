"""Ownership of long-lived resources (L17).

The application lifespan owns every client it creates (later, the Anthropic client and the database): each is
registered here when created and closed exactly once, newest first, when the host shuts down. Nothing else closes
them, and nothing creates its own long-lived client per request.
"""

from __future__ import annotations

from collections.abc import Awaitable, Callable


class Resources:
    def __init__(self) -> None:
        self._closers: list[tuple[str, Callable[[], Awaitable[None]]]] = []
        self.closed: list[str] = []

    def own(self, name: str, close: Callable[[], Awaitable[None]]) -> None:
        """Registers a resource to close at shutdown."""
        self._closers.append((name, close))

    async def close_all(self) -> None:
        """Closes every owned resource, newest first; one failing close does not stop the others."""
        errors: list[Exception] = []
        while self._closers:
            name, close = self._closers.pop()
            try:
                await close()
                self.closed.append(name)
            except Exception as error:
                errors.append(error)
        if errors:
            raise ExceptionGroup("Some resources failed to close.", errors)
