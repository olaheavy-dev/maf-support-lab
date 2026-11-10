"""The agents' only write tools (L37). Each is a native approval-required MAF tool: the framework pauses for the
customer's decision instead of running it. When it does run (after approval), it can only perform the operation the
customer approved, with exactly these arguments, once (OperationService.execute_approved). None takes a customer id or
a "confirmed" flag: approval comes from the customer's decision, never from the model.
"""

from __future__ import annotations

from typing import Annotated, Any

from agent_framework import FunctionTool, tool
from pydantic import Field

from support_agent.identity import current_principal
from support_agent.operations import Kind, OperationService, WriteError


async def _run(operations: OperationService, kind: Kind, arguments: dict[str, Any]) -> dict[str, Any]:
    try:
        return {
            "status": "committed",
            "result": await operations.execute_approved(current_principal(), kind, arguments),
        }
    except WriteError as error:
        status = (
            "invalid_arguments"
            if error.code == "invalid_arguments"
            else "not_approved"
            if error.code == "not_approved"
            else "failed"
        )
        return {"status": status, "detail": error.detail}


def cancel_tool(operations: OperationService) -> FunctionTool:
    @tool(
        name=Kind.CANCEL_ORDER,
        description="Cancel one of the current customer's orders. Requires the customer's approval; only orders still processing can be cancelled.",
        approval_mode="always_require",
    )
    async def cancel_order(
        order_id: Annotated[str, Field(description="The order id, in the form ORD-1234.")],
    ) -> dict[str, Any]:
        return await _run(operations, Kind.CANCEL_ORDER, {"order_id": order_id})

    return cancel_order


def refund_tool(operations: OperationService) -> FunctionTool:
    @tool(
        name=Kind.REFUND_ORDER,
        description="Refund one of the current customer's delivered orders in full. Requires the customer's approval.",
        approval_mode="always_require",
    )
    async def refund_order(
        order_id: Annotated[str, Field(description="The order id, in the form ORD-1234.")],
        reason: Annotated[str, Field(description="The customer's reason, in their words.")],
    ) -> dict[str, Any]:
        return await _run(operations, Kind.REFUND_ORDER, {"order_id": order_id, "reason": reason})

    return refund_order


def ticket_tool(operations: OperationService) -> FunctionTool:
    @tool(
        name=Kind.CREATE_TICKET,
        description="Open a support ticket for a problem the knowledge base could not solve. Requires the customer's approval.",
        approval_mode="always_require",
    )
    async def create_ticket(
        subject: Annotated[str, Field(description="A short subject line.")],
        description: Annotated[str, Field(description="What is wrong, in the customer's words.")],
        priority: Annotated[str, Field(description="low, normal, or high.")] = "normal",
        related_order_id: Annotated[str | None, Field(description="A related order id (ORD-1234), if any.")] = None,
    ) -> dict[str, Any]:
        return await _run(
            operations,
            Kind.CREATE_TICKET,
            {
                "subject": subject,
                "description": description,
                "priority": priority,
                "related_order_id": related_order_id,
            },
        )

    return create_ticket
