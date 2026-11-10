"""The support workflow (MAF Workflows, L18, porting L07/L08): triage, then a deterministic switch to one executor.

    triage ─┬─ orders, no order id     ─▶ ask-for-order-id
            ├─ cancel/refund/ticket    ─▶ write-not-available   (no write tools until L37)
            ├─ orders                  ─▶ orders-specialist
            ├─ billing                 ─▶ billing-specialist
            ├─ technical               ─▶ technical-specialist
            └─ default                 ─▶ unrouted              (invalid or ambiguous triage)

Every ending sends its reply to one ``reply`` executor, the workflow's single output. A fresh workflow is built per
turn, so concurrent turns never share executor state. The DevUI variant swaps only the two ends (text in, text out).
"""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass
from typing import Any, Never

from agent_framework import (
    Case,
    Default,
    Executor,
    Message,
    Workflow,
    WorkflowBuilder,
    WorkflowContext,
    WorkflowEvent,
    handler,
)

from support_agent.activity import begin_activity
from support_agent.agents import SPECIALISTS, SupportAgents
from support_agent.routing import TriageDecision, TriageOutcome, TurnRoute, UnroutedReason, parse, route

WORKFLOW_NAME = "support-routing"
ASK_FOR_ORDER_ID = "Which order do you mean? Please share the order id, for example ORD-1234."
UNROUTED = "I'm not sure what you need. Is this about an order, billing, or a technical problem?"
WRITE_NOT_AVAILABLE = (
    "I can't make changes like that here yet. Nothing has been changed. I can pass this to a human support agent."
)
FALLBACK = "Sorry, I couldn't produce an answer. Please try again."


@dataclass(frozen=True, slots=True)
class RoutingRequest:
    message: str
    conversation_id: str | None = None


@dataclass(frozen=True, slots=True)
class SupportReply:
    route: TurnRoute
    kind: str
    text: str
    verified_order_id: str | None = None


@dataclass(frozen=True, slots=True)
class Progress:
    """A progress step (a stage and maybe a route; never message content), carried by a workflow ``data`` event."""

    stage: str
    route: dict[str, Any] | None = None


def progress(stage: str, route: dict[str, Any] | None = None) -> WorkflowEvent[Progress]:
    return WorkflowEvent("data", data=Progress(stage, route))


class TriageExecutor(Executor):
    """Runs the triage model, validates its output against the full contract schema, and applies the routing rules."""

    def __init__(self, agents: SupportAgents) -> None:
        super().__init__(id="triage")
        self._agents = agents

    @handler
    async def classify(self, request: RoutingRequest, ctx: WorkflowContext[TriageOutcome]) -> None:
        await ctx.add_event(progress("triage"))
        response = await self._agents.triage.run(request.message, options={"response_format": TriageDecision})
        decision = parse(response.text)  # the model's output is untrusted until it passes the full schema
        outcome = TriageOutcome(request.message, decision, route(decision), request.conversation_id)
        await ctx.add_event(progress("routed", route=outcome.route.to_contract()))
        await ctx.send_message(outcome)


class AskForOrderIdExecutor(Executor):
    def __init__(self) -> None:
        super().__init__(id="ask-for-order-id")

    @handler
    async def ask(self, outcome: TriageOutcome, ctx: WorkflowContext[SupportReply]) -> None:
        await ctx.send_message(SupportReply(outcome.route, "clarification", ASK_FOR_ORDER_ID))


class WriteNotAvailableExecutor(Executor):
    """Cancel, refund, and ticket requests: no write tool exists until the approval gate (L37). No model call."""

    def __init__(self) -> None:
        super().__init__(id="write-not-available")

    @handler
    async def decline(self, outcome: TriageOutcome, ctx: WorkflowContext[SupportReply]) -> None:
        await ctx.send_message(SupportReply(outcome.route, "escalation_offer", WRITE_NOT_AVAILABLE))


class UnroutedExecutor(Executor):
    """The fallback for invalid or ambiguous triage: never guess a route; ask instead."""

    def __init__(self) -> None:
        super().__init__(id="unrouted")

    @handler
    async def clarify(self, outcome: TriageOutcome, ctx: WorkflowContext[SupportReply]) -> None:
        reported = (
            outcome.route if not outcome.route.target else TurnRoute(unrouted_reason=UnroutedReason.AMBIGUOUS_REQUEST)
        )
        await ctx.send_message(SupportReply(reported, "clarification", UNROUTED))


class SpecialistExecutor(Executor):
    """Runs the routed specialist on the customer's original message (never a model-written summary)."""

    def __init__(self, agents: SupportAgents, target: str) -> None:
        super().__init__(id=f"{target}-specialist")
        self._agents = agents
        self._target = target

    @handler
    async def answer(self, outcome: TriageOutcome, ctx: WorkflowContext[SupportReply]) -> None:
        await ctx.add_event(progress("specialist", route={"status": "routed", "target": self._target}))
        with begin_activity() as activity:
            response = await self._agents.specialist(self._target).run(
                [Message(role="user", contents=[outcome.original_message])]
            )
        # The reply kind comes from what the tools recorded, not from the model's wording.
        reply = response.text.strip() or FALLBACK
        await ctx.send_message(SupportReply(outcome.route, activity.reply_kind(), reply, activity.verified_order_id))


class ReplyExecutor(Executor):
    """The API workflow's single output: the finished reply."""

    def __init__(self) -> None:
        super().__init__(id="reply")

    @handler
    async def finish(self, reply: SupportReply, ctx: WorkflowContext[Never, SupportReply]) -> None:
        await ctx.yield_output(reply)


class ChatInputExecutor(Executor):
    """DevUI only: plain text in. DevUI workflow chats are stateless (no conversation id)."""

    def __init__(self) -> None:
        super().__init__(id="chat-input")

    @handler
    async def accept(self, text: str, ctx: WorkflowContext[RoutingRequest]) -> None:
        await ctx.send_message(RoutingRequest(text))


class ChatReplyExecutor(Executor):
    """DevUI only: the reply as text, with the routing result on a first line, e.g. ``[route: billing | reply: answer]``."""

    def __init__(self) -> None:
        super().__init__(id="reply")

    @handler
    async def finish(self, reply: SupportReply, ctx: WorkflowContext[Never, str]) -> None:
        where = reply.route.target or f"unrouted ({reply.route.unrouted_reason})"
        await ctx.yield_output(f"[route: {where} | reply: {reply.kind}]\n{reply.text}")


def build_workflow(agents: SupportAgents, *, for_chat: bool = False) -> Workflow:
    triage = TriageExecutor(agents)
    ask = AskForOrderIdExecutor()
    write = WriteNotAvailableExecutor()
    unrouted = UnroutedExecutor()
    specialists = {target: SpecialistExecutor(agents, target) for target in SPECIALISTS}
    reply: Executor = ChatReplyExecutor() if for_chat else ReplyExecutor()
    start: Executor = ChatInputExecutor() if for_chat else triage

    def routed_to(target: str) -> Callable[[Any], bool]:
        def condition(outcome: Any) -> bool:
            return isinstance(outcome, TriageOutcome) and outcome.route.target == target

        return condition

    builder = WorkflowBuilder(
        name=WORKFLOW_NAME,
        description="Triage, then one read-only specialist (or a clarification).",
        start_executor=start,
        output_from=[reply],
    )
    if for_chat:
        builder.add_edge(start, triage)
    builder.add_switch_case_edge_group(
        triage,
        [
            Case(lambda outcome: isinstance(outcome, TriageOutcome) and outcome.is_missing_order_id, ask),
            Case(lambda outcome: isinstance(outcome, TriageOutcome) and outcome.is_write, write),
            *(Case(routed_to(target), executor) for target, executor in specialists.items()),
            Default(unrouted),
        ],
    )
    for ending in (ask, write, unrouted, *specialists.values()):
        builder.add_edge(ending, reply)
    return builder.build()
