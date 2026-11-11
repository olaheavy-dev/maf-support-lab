"""The support workflow (MAF Workflows, L18, porting L07/L08): triage, then a deterministic switch to one executor.

    triage ─┬─ orders, no order id     ─▶ ask-for-order-id
            ├─ orders                  ─▶ orders-specialist
            ├─ billing                 ─▶ billing-specialist
            ├─ technical               ─▶ technical-specialist
            └─ default                 ─▶ unrouted              (invalid or ambiguous triage)

Every ending sends its reply to one ``reply`` executor, the workflow's single output. A fresh workflow is built per
turn, so concurrent turns never share executor state. The DevUI variant swaps only the two ends (text in, text out).
"""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass, field
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
from support_agent.approval_gate import gate
from support_agent.operations import OperationService
from support_agent.routing import TriageDecision, TriageOutcome, TurnRoute, UnroutedReason, parse, route
from support_agent.sessions import ConversationContext, ConversationSessions, context_note
from support_agent.telemetry import triage_decisions
from support_agent.turn_control import Delta, Progress

WORKFLOW_NAME = "support-routing"
ASK_FOR_ORDER_ID = "Which order do you mean? Please share the order id, for example ORD-1234."
UNROUTED = "I'm not sure what you need. Is this about an order, billing, or a technical problem?"
FALLBACK = "Sorry, I couldn't produce an answer. Please try again."


@dataclass(frozen=True, slots=True)
class RoutingRequest:
    message: str
    conversation_id: str | None = None
    context: ConversationContext = field(default_factory=ConversationContext)
    sessions: ConversationSessions | None = None  # None: stateless (DevUI workflow chats)
    turn_id: str | None = None


@dataclass(frozen=True, slots=True)
class SupportReply:
    route: TurnRoute
    kind: str
    text: str
    verified_order_id: str | None = None
    pending_operation_id: str | None = None
    operation_ids: tuple[str, ...] = ()


def progress(stage: str, route: dict[str, Any] | None = None, tool: str | None = None) -> WorkflowEvent[Progress]:
    """A progress step as a native workflow ``data`` event (stage, route, tool name; never message content)."""
    return WorkflowEvent("data", data=Progress(stage, route, tool))


def delta(text: str) -> WorkflowEvent[Delta]:
    """Reply text as it arrives, as a native workflow ``data`` event (L20). Display only."""
    return WorkflowEvent("data", data=Delta(text))


class TriageExecutor(Executor):
    """Runs the triage model, validates its output against the full contract schema, and applies the routing rules."""

    def __init__(self, agents: SupportAgents) -> None:
        super().__init__(id="triage")
        self._agents = agents

    @handler
    async def classify(self, request: RoutingRequest, ctx: WorkflowContext[TriageOutcome]) -> None:
        await ctx.add_event(progress("triage"))
        # Triage is stateless: it classifies this one message. A follow-up gets the previous route as a short,
        # code-written hint; the message itself is still what gets classified.
        messages = [Message(role="user", contents=[request.message])]
        if request.context.last_target is not None:
            messages.append(
                context_note(f"The previous request in this conversation was routed to {request.context.last_target}.")
            )
        response = await self._agents.triage.run(messages, options={"response_format": TriageDecision})
        decision = parse(response.text)  # the model's output is untrusted until it passes the full schema
        outcome = TriageOutcome(
            request.message,
            decision,
            route(decision),
            request.conversation_id,
            request.context,
            request.sessions,
            request.turn_id,
        )
        triage_decisions.add(
            1,
            {
                "support.triage.outcome": "routed" if outcome.route.target else "unrouted",
                "support.triage.target": outcome.route.target or "none",
                "support.triage.unrouted_reason": str(outcome.route.unrouted_reason or "none"),
            },
        )
        await ctx.add_event(progress("routed", route=outcome.route.to_contract()))
        await ctx.send_message(outcome)


class AskForOrderIdExecutor(Executor):
    def __init__(self) -> None:
        super().__init__(id="ask-for-order-id")

    @handler
    async def ask(self, outcome: TriageOutcome, ctx: WorkflowContext[SupportReply]) -> None:
        await ctx.send_message(SupportReply(outcome.route, "clarification", ASK_FOR_ORDER_ID))


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

    def __init__(self, agents: SupportAgents, operations: OperationService, target: str) -> None:
        super().__init__(id=f"{target}-specialist")
        self._agents = agents
        self._operations = operations
        self._target = target

    @handler
    async def answer(self, outcome: TriageOutcome, ctx: WorkflowContext[SupportReply]) -> None:
        await ctx.add_event(progress("specialist", route={"status": "routed", "target": self._target}))
        agent = self._agents.specialist(self._target)
        # The conversation's own native session for this specialist (L19): its bounded history comes back each turn.
        session = outcome.sessions.for_agent(self._target, agent) if outcome.sessions is not None else None
        messages = [Message(role="user", contents=[outcome.original_message])]
        if outcome.order_id is None and (verified := outcome.context.verified_order_id) is not None:
            # A follow-up that names no order gets the order a tool verified earlier, as a trusted note for this turn.
            # It comes right after the customer's message (a mid-conversation system message is only accepted there).
            messages.append(
                context_note(
                    f'Verified context: earlier in this conversation the customer\'s order {verified} was found. If they refer to "it" or "that order", they mean {verified}.'
                )
            )
        with begin_activity() as activity:
            # Streamed (L20): the model's text goes out as it arrives; tool calls show as progress. The stream is
            # closed deterministically, also when the turn is cancelled mid-stream.
            async with agent.run(messages, session=session, stream=True) as stream:
                async for update in stream:
                    for content in update.contents:
                        if content.type == "function_call" and content.name:
                            await ctx.add_event(progress("tool_call", tool=content.name))
                    if update.text:
                        await ctx.add_event(delta(update.text))
                response = await stream.get_final_response()
            # A write tool pauses natively for approval; the gate binds it to an operation or declines it (L37).
            gated = await gate(agent, session, response, outcome, self._target, self._operations)
        if outcome.sessions is not None:
            outcome.sessions.save(self._target)  # durable history (L38); a failure here fails the turn
        verified = activity.verified_order_id
        if gated.proposed is not None:
            operation = gated.proposed
            await ctx.add_event(progress("awaiting_approval"))
            text = f"I can do this for you: {operation.summary} Please approve or reject."
            await ctx.send_message(
                SupportReply(
                    outcome.route, "approval_request", text, verified, operation.operation_id, (operation.operation_id,)
                )
            )
            return
        if gated.declined_code in ("not_eligible", "not_found"):
            # A declined write: the factual reason, written by code (never inferred from the model's wording).
            text = (
                f"I can't do that: {gated.declined_reason} Nothing has been changed."
                if gated.declined_code == "not_eligible"
                else "I couldn't find that order on your account, so nothing has been changed."
            )
            await ctx.send_message(SupportReply(outcome.route, gated.declined_code, text, verified))
            return
        reply = gated.response.text.strip() or FALLBACK
        # The reply kind comes from what the tools recorded, not from the model's wording.
        kind = "clarification" if gated.declined_code else activity.reply_kind()
        await ctx.send_message(SupportReply(outcome.route, kind, reply, verified))


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


def build_workflow(agents: SupportAgents, operations: OperationService, *, for_chat: bool = False) -> Workflow:
    triage = TriageExecutor(agents)
    ask = AskForOrderIdExecutor()
    unrouted = UnroutedExecutor()
    specialists = {target: SpecialistExecutor(agents, operations, target) for target in SPECIALISTS}
    reply: Executor = ChatReplyExecutor() if for_chat else ReplyExecutor()
    start: Executor = ChatInputExecutor() if for_chat else triage

    def routed_to(target: str) -> Callable[[Any], bool]:
        def condition(outcome: Any) -> bool:
            return isinstance(outcome, TriageOutcome) and outcome.route.target == target

        return condition

    builder = WorkflowBuilder(
        name=WORKFLOW_NAME,
        description="Triage, then one specialist (reads, or one approval-gated write), or a clarification.",
        start_executor=start,
        output_from=[reply],
    )
    if for_chat:
        builder.add_edge(start, triage)
    builder.add_switch_case_edge_group(
        triage,
        [
            Case(lambda outcome: isinstance(outcome, TriageOutcome) and outcome.is_missing_order_id, ask),
            *(Case(routed_to(target), executor) for target, executor in specialists.items()),
            Default(unrouted),
        ],
    )
    for ending in (ask, unrouted, *specialists.values()):
        builder.add_edge(ending, reply)
    return builder.build()
