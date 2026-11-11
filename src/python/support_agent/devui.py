"""Development-only Python DevUI (L18): the read-only specialists and the support workflow, for inspection.

It runs as its own process on loopback (``uv run python -m support_agent.devui``; the AppHost starts it in
development). It uses the same agents, tools, and guarded services as the API. DevUI has no request identity, so
this process serves one configured development customer, never a customer chosen by the client or the model. It
refuses to start outside development.
"""

from __future__ import annotations

import os
from typing import Any

from support_agent.agents import create_agents
from support_agent.database import Database
from support_agent.fixtures import Fixtures
from support_agent.identity import Principal, set_process_principal
from support_agent.model_provider import create_client
from support_agent.operations import build_operations
from support_agent.services import Services
from support_agent.settings import Environment, Settings
from support_agent.telemetry import configure as configure_telemetry
from support_agent.workflow import build_workflow


def entities(settings: Settings) -> list[Any]:
    """What DevUI lists: the three read-only specialists and the support-routing workflow."""
    if settings.app_environment is not Environment.DEVELOPMENT or not settings.identity.development_customer_id:
        raise RuntimeError("DevUI is only available in development.")
    set_process_principal(Principal(settings.identity.development_customer_id))
    # DevUI chats are stateless and never decide approvals; it gets its own store, never the API's file.
    database = Database(settings.storage.path.with_name("devui.db"))
    services = Services.create(Fixtures.load(), database)
    operations = build_operations(services.state, database)
    agents = create_agents(create_client(settings), services, operations)
    return [*agents.specialists.values(), build_workflow(agents, operations, for_chat=True)]


def main() -> None:
    from agent_framework.devui import serve

    port = int(os.environ.get("PORT", "5290"))
    settings = Settings()
    # Our own providers, under this process's own service name (L40). DevUI's instrumentation switch stays off: it
    # would turn on sensitive capture unconditionally, and its own provider setup is marked done by configure().
    configure_telemetry(settings, default_service_name="support-devui-python")
    # Loopback only, so DevUI's own bearer-token auth may stay off (it refuses that on other hosts).
    serve(
        entities=entities(settings),
        host="127.0.0.1",
        port=port,
        auth_enabled=False,
        auto_open=False,
        instrumentation_enabled=False,
    )


if __name__ == "__main__":
    main()
