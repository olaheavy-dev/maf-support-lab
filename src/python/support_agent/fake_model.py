"""A deterministic stand-in for Claude (fake mode, L18), ported from the .NET fake model's behaviour.

It is composed from MAF's real chat-client layers (function invocation, middleware, telemetry), so tool calls run
through the framework's actual tool loop. Triage calls (those asking for structured output) get keyword triage; other
calls pick the fitting tool among those the agent actually has, then answer from its result. Markers in the
customer's text force failure paths for tests: see FAILURE_TRIGGER and the triage markers.
"""

from __future__ import annotations

import json
import re
from collections.abc import AsyncGenerator, Awaitable, Callable, Mapping, Sequence
from typing import Any, cast

from agent_framework import (
    BaseChatClient,
    ChatMiddlewareLayer,
    ChatResponse,
    ChatResponseUpdate,
    Content,
    FunctionInvocationLayer,
    Message,
    ResponseStream,
)
from agent_framework.observability import ChatTelemetryLayer

from support_agent import tools as names

FAILURE_TRIGGER = "[fake:model-error]"
MALFORMED_TRIAGE_TRIGGER = "[fake:triage-malformed]"
UNKNOWN_ROUTE_TRIGGER = "[fake:triage-unknown-route]"

ORDER_ID = re.compile(r"ORD-[0-9]{4}", re.IGNORECASE)
INVOICE_ID = re.compile(r"INV-[0-9]{4}", re.IGNORECASE)

Script = Callable[[Sequence[Message], Mapping[str, Any]], ChatResponse]
Triage = Callable[[str], str]


class FakeModelError(RuntimeError):
    """Raised when a test asks the fake model to fail."""


def last_user_text(messages: Sequence[Message]) -> str:
    return next((message.text for message in reversed(messages) if message.role == "user"), "")


def _contains_any(text: str, *words: str) -> bool:
    return any(word in text for word in words)


def default_triage(user_text: str) -> str:
    """Keyword triage that behaves like a well-behaved classifier and returns the contract's triage-decision JSON."""
    if MALFORMED_TRIAGE_TRIGGER in user_text:
        return "Sure! This looks like an order question."
    order_id = ORDER_ID.search(user_text)
    text = user_text.upper()
    target, action = (
        ("shipping", "order_status")
        if UNKNOWN_ROUTE_TRIGGER in user_text
        else ("billing", "refund_order")
        if "REFUND" in text
        else ("billing", "invoice")
        if _contains_any(text, "INVOICE", "BILL", "RECEIPT")
        else ("billing", "payment_history")
        if _contains_any(text, "PAYMENT", "CHARGE", "PAID")
        else ("orders", "cancel_order")
        if "CANCEL" in text
        else ("technical", "create_ticket")
        if "TICKET" in text
        else ("orders", "order_status")
        if order_id or _contains_any(text, "ORDER", "DELIVER", "PACKAGE", "SHIPP", "TRACK")
        else ("technical", "troubleshooting")
        if _contains_any(
            text,
            "ERROR",
            "CRASH",
            "NOT WORKING",
            "BROKEN",
            "LOGIN",
            "LOG IN",
            "RESET",
            "WON'T",
            "CRACKL",
            "WIFI",
            "WI-FI",
            "FIRMWARE",
            "AUDIO",
            "BLUETOOTH",
            "WOBBL",
            "SETUP",
            "CONNECT",
            "STATIC",
        )
        else ("orders", "other")
    )
    return json.dumps(
        {
            "target": target,
            "requested_action": action,
            "order_id": order_id.group(0).upper() if order_id else None,
            "summary": "Customer support request.",
            "customer_sentiment": "frustrated"
            if _contains_any(text, "ANGRY", "FRUSTRAT", "!!", "RIDICULOUS")
            else "neutral",
            "confidence": 0.4 if action == "other" else 0.9,
        }
    )


def text(reply: str) -> ChatResponse:
    return ChatResponse(messages=[Message(role="assistant", contents=[reply])], model="fake-model")


def tool_call(name: str, arguments: Mapping[str, Any]) -> ChatResponse:
    call = Content.from_function_call(
        call_id=f"call_{name}_{abs(hash(json.dumps(arguments, sort_keys=True))) % 10**8}",
        name=name,
        arguments=dict(arguments),
    )
    return ChatResponse(messages=[Message(role="assistant", contents=[call])], model="fake-model")


def _tool_names(options: Mapping[str, Any]) -> set[str]:
    tools: Sequence[Any] = options.get("tools") or []
    return {str(getattr(tool, "name", "")) for tool in tools}


def _tool_name_for(messages: Sequence[Message], call_id: str | None) -> str | None:
    for message in reversed(messages):
        for content in message.contents:
            if content.type == "function_call" and content.call_id == call_id:
                return content.name
    return None


def _as_dict(result: Any) -> dict[str, Any]:
    try:
        data: Any = json.loads(result) if isinstance(result, str) else result
    except ValueError:
        return {}
    return cast(dict[str, Any], data) if isinstance(data, dict) else {}


def _describe(tool: str | None, result: Any) -> str:
    if isinstance(result, str) and "rejected" in result.lower():
        return "Understood, I won't go ahead with that."
    data = _as_dict(result)
    if tool in ("cancel_order", "refund_order", "create_ticket"):
        outcome = cast(dict[str, Any], data.get("result") or {})
        if data.get("status") != "committed":
            return f"I couldn't do that: {data.get('detail')}"
        if tool == "cancel_order":
            return f"Done: order {outcome.get('order_id')} is cancelled."
        if tool == "refund_order":
            return f"Done: refund {outcome.get('refund_id')} for order {outcome.get('order_id')} has been recorded."
        return f"Done: support ticket {outcome.get('ticket_id')} is open."
    if tool == names.INVOICE:
        match data.get("lookup"):
            case "found":
                return f"Invoice {data.get('invoice_id')} for order {data.get('order_id')}: total {data.get('total')}, paid with {data.get('payment_method')}."
            case "not_found":
                return f"I couldn't find {data.get('reference')} on your account. Please check the id."
            case _:
                return "Please share the order id (ORD-1234) or invoice id (INV-1234)."
    if tool == names.PAYMENT_HISTORY:
        payments = cast(list[dict[str, Any]], data.get("payments") or [])
        return (
            "Your payments: "
            + "; ".join(f"{p['payment_id']} {p['kind']} of {p['amount']} for {p['order_id']}" for p in payments)
            + "."
        )
    if tool == names.KNOWLEDGE_SEARCH:
        articles = cast(list[dict[str, Any]], data.get("articles") or [])
        if not articles:
            return "I couldn't find a documented fix for that. I can pass this to a human support agent."
        article = articles[0]
        steps = cast(list[str], article["steps"])
        return f"From {article['article_id']} ({article['title']}): " + " ".join(steps)
    match data.get("lookup"):
        case "found":
            return f"Order {data.get('order_id')} is {data.get('order_status')}."
        case "not_found":
            return f"I couldn't find order {data.get('order_id')} on your account. Please check the order id."
        case _:
            return "That doesn't look like an order id. Please share it in the form ORD-1234."


def default_script(messages: Sequence[Message], options: Mapping[str, Any]) -> ChatResponse:
    """Calls the fitting tool among those the agent actually has, then answers from its result."""
    results = [content for content in (messages[-1].contents if messages else []) if content.type == "function_result"]
    if results:
        return text(_describe(_tool_name_for(messages, results[-1].call_id), results[-1].result))

    user_text = last_user_text(messages)
    if FAILURE_TRIGGER in user_text:
        raise FakeModelError("The fake model failed because the message asked it to.")
    tools = _tool_names(options) or {names.ORDER_STATUS}
    # Like a real model, use the trusted context note when the customer refers to "it" without an id.
    notes = " ".join(message.text for message in messages if message.author_name == "support-context")
    order_id = ORDER_ID.search(user_text) or ORDER_ID.search(notes)
    invoice_id = INVOICE_ID.search(user_text)
    upper = user_text.upper()

    # Write requests first, like a model that does what was asked: the framework then pauses for approval (L37).
    if "cancel_order" in tools and "CANCEL" in upper and order_id:
        return tool_call("cancel_order", {"order_id": order_id.group(0).upper()})
    if "refund_order" in tools and "REFUND" in upper and order_id:
        return tool_call("refund_order", {"order_id": order_id.group(0).upper(), "reason": user_text})
    if "create_ticket" in tools and "TICKET" in upper:
        return tool_call(
            "create_ticket", {"subject": "Support request", "description": user_text, "priority": "normal"}
        )
    if names.KNOWLEDGE_SEARCH in tools:
        return tool_call(names.KNOWLEDGE_SEARCH, {"query": user_text})
    if names.PAYMENT_HISTORY in tools and _contains_any(upper, "PAYMENT", "HISTORY", "CHARGE", "PAID"):
        return tool_call(names.PAYMENT_HISTORY, {})
    if names.INVOICE in tools:
        reference = invoice_id or order_id
        if reference is None:
            return text("Which invoice do you mean? Please share the order id (ORD-1234) or invoice id (INV-1234).")
        return tool_call(names.INVOICE, {"reference": reference.group(0).upper()})
    if names.ORDER_STATUS in tools and order_id:
        return tool_call(names.ORDER_STATUS, {"order_id": order_id.group(0).upper()})
    return text("Which order do you mean? Please share the order id, for example ORD-1234.")


def check_tool_pairing(messages: Sequence[Message]) -> None:
    """Rejects what Anthropic rejects (HTTP 400): every tool result must answer a tool call made in the message just
    before it. The fake enforces this so tests catch history or resume bugs that only a real provider would expose."""
    for index, message in enumerate(messages):
        results = [content.call_id for content in message.contents if content.type == "function_result"]
        if not results:
            continue
        previous = messages[index - 1] if index > 0 else None
        calls = (
            {content.call_id for content in previous.contents if content.type == "function_call"}
            if previous is not None and previous.role == "assistant"
            else set[str | None]()
        )
        missing = [call_id for call_id in results if call_id not in calls]
        if missing:
            raise FakeModelError(f"A tool result answers no tool call in the previous assistant message: {missing}")


class ScriptedChatClient(BaseChatClient[Any]):
    """The provider-level fake: triage on structured-output calls, the script otherwise."""

    def __init__(self, script: Script | None = None, triage: Triage | None = None, **kwargs: Any) -> None:
        super().__init__(**kwargs)
        self.script: Script = script or default_script
        self.triage: Triage = triage or default_triage
        self.calls = 0
        # Tests may hold a call here (for example to observe a running turn); awaited before the script runs.
        self.before_call: Callable[[], Awaitable[None]] | None = None

    async def _respond(self, messages: Sequence[Message], options: Mapping[str, Any]) -> ChatResponse:
        self.calls += 1
        check_tool_pairing(messages)
        if self.before_call is not None:
            await self.before_call()
        if options.get("response_format") is not None:
            return text(self.triage(last_user_text(messages)))
        return self.script(messages, options)

    def _inner_get_response(
        self, *, messages: Sequence[Message], stream: bool, options: Mapping[str, Any], **kwargs: Any
    ) -> Awaitable[ChatResponse] | ResponseStream[ChatResponseUpdate, ChatResponse]:
        if not stream:
            return self._respond(messages, options)

        async def updates() -> AsyncGenerator[ChatResponseUpdate]:
            response = await self._respond(messages, options)
            for message in response.messages:
                yield ChatResponseUpdate(role="assistant", contents=list(message.contents))

        return ResponseStream(updates(), finalizer=ChatResponse.from_updates)


class FakeChatClient(
    FunctionInvocationLayer[Any], ChatMiddlewareLayer[Any], ChatTelemetryLayer[Any], ScriptedChatClient
):
    """The fake model as agents use it: the same layers as the real Anthropic client, over the scripted core."""
