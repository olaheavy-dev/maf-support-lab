"""Runs the host on its own, outside Aspire: ``uv run python -m support_agent``.

Port and host come from ``PORT`` (default 5280) and ``HOST`` (default 127.0.0.1). Configuration comes from the
environment; see README.md for the minimal development settings.
"""

from __future__ import annotations

import os

import uvicorn


def main() -> None:
    uvicorn.run(
        "support_agent.app:app", host=os.environ.get("HOST", "127.0.0.1"), port=int(os.environ.get("PORT", "5280"))
    )


if __name__ == "__main__":
    main()
