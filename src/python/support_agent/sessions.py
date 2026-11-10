"""Per-conversation state for the agents (L19, porting L09).

The agents are reusable definitions shared by every conversation; a conversation's history lives only in its own
native MAF ``AgentSession`` per specialist, created on first use and reused on later turns. There is no global mutable
history. ``BoundedHistory`` keeps a session's history short and plain, and ``ConversationContext`` carries only facts
a tool verified, never something the customer or the model merely mentioned.
"""

from __future__ import annotations

import json
from collections.abc import Sequence
from dataclasses import dataclass, field
from typing import Any

from agent_framework import AgentSession, InMemoryHistoryProvider, Message, SupportsAgentRun

from support_agent.database import Database

CONTEXT_AUTHOR = "support-context"
MAX_MESSAGES = 12


def context_note(text: str) -> Message:
    """A trusted, code-written note for one turn. Sent after the customer's message; never stored in history."""
    return Message(role="system", contents=[text], author_name=CONTEXT_AUTHOR)


def is_context_note(message: Message) -> bool:
    return message.author_name == CONTEXT_AUTHOR


def _is_plain(message: Message) -> bool:
    """Plain conversation text only: no tool calls or results, which would be orphaned when the history is trimmed."""
    return all(content.type == "text" for content in message.contents) and bool(message.text.strip())


class BoundedHistory(InMemoryHistoryProvider):
    """Stores only plain user and assistant messages (no tool traffic, no context notes), at most MAX_MESSAGES."""

    async def save_messages(
        self, session_id: str | None, messages: Sequence[Message], *, state: dict[str, Any] | None = None, **kwargs: Any
    ) -> None:
        keep = [
            message
            for message in messages
            if message.role in ("user", "assistant") and not is_context_note(message) and _is_plain(message)
        ]
        await super().save_messages(session_id, keep, state=state, **kwargs)
        if state is not None and len(state.get("messages", [])) > MAX_MESSAGES:
            state["messages"] = state["messages"][-MAX_MESSAGES:]


@dataclass(frozen=True, slots=True)
class ConversationContext:
    """What a conversation remembers between turns for routing: application state, not chat history."""

    last_target: str | None = None
    verified_order_id: str | None = None

    def after(self, target: str | None, verified_order_id: str | None) -> ConversationContext:
        """A newly verified order replaces the old one; otherwise the old one stays. The same goes for the route."""
        return ConversationContext(target or self.last_target, verified_order_id or self.verified_order_id)

    def to_json(self) -> str:
        return json.dumps({"last_target": self.last_target, "verified_order_id": self.verified_order_id})

    @classmethod
    def from_json(cls, text: str) -> ConversationContext:
        data: dict[str, Any] = json.loads(text)
        return cls(data.get("last_target"), data.get("verified_order_id"))


@dataclass
class ConversationSessions:
    """One native session per specialist for one conversation. Reached only through an owned conversation.

    Durable since L38: a session is loaded from the database on first use and saved after each run that changed it,
    so a conversation's history survives a restart. Without a database (unit tests), sessions live in memory only.
    """

    conversation_id: str | None = None
    database: Database | None = None
    sessions: dict[str, AgentSession] = field(default_factory=dict[str, AgentSession])

    def for_agent(self, name: str, agent: SupportsAgentRun) -> AgentSession:
        if name not in self.sessions:
            stored = (
                self.database.query(
                    "SELECT json FROM agent_sessions WHERE conversation_id = ? AND agent = ?",
                    (self.conversation_id, name),
                )
                if self.database is not None
                else []
            )
            self.sessions[name] = AgentSession.from_dict(json.loads(stored[0][0])) if stored else agent.create_session()
        return self.sessions[name]

    def save(self, name: str) -> None:
        """Stores one specialist's session (its bounded history) after a run."""
        if self.database is None or (session := self.sessions.get(name)) is None:
            return
        self.database.execute(
            "INSERT INTO agent_sessions(conversation_id, agent, json) VALUES (?, ?, ?) "
            "ON CONFLICT(conversation_id, agent) DO UPDATE SET json = excluded.json",
            (self.conversation_id, name, json.dumps(session.to_dict())),
        )

    def history(self, name: str) -> list[Message]:
        """For tests and diagnostics: the stored history of one specialist's session."""
        session = self.sessions.get(name)
        if session is None:
            return []
        # MAF keeps each history provider's messages under that provider's source id in the session state.
        stored: dict[str, Any] = session.state.get(BoundedHistory.DEFAULT_SOURCE_ID, {})
        messages: list[Message] = list(stored.get("messages", []))
        return messages
