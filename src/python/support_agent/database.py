"""The local durable store (L38, porting L11): one SQLite file per backend and per run.

The Python backend never shares this file with .NET, and each test or evaluation run gets its own (``STORAGE__PATH``).
Writes inside :meth:`Database.atomically` share one ``BEGIN IMMEDIATE`` transaction and commit together or not at all;
other calls use their own short-lived connection, so there is nothing long-lived to close. Single process only:
SQLite serializes writers, and guarded updates keep data correct, but per-conversation turn coordination is in-process.
"""

from __future__ import annotations

import sqlite3
from collections.abc import Generator, Sequence
from contextlib import contextmanager
from contextvars import ContextVar
from pathlib import Path
from typing import Any

SCHEMA = """
CREATE TABLE IF NOT EXISTS counters (name TEXT PRIMARY KEY, value INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS changed_orders (order_id TEXT PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS payments (payment_id TEXT PRIMARY KEY, customer_id TEXT NOT NULL, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS tickets (ticket_id TEXT PRIMARY KEY, customer_id TEXT NOT NULL, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS conversations (conversation_id TEXT PRIMARY KEY, owner TEXT NOT NULL, created_at TEXT NOT NULL, context_json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS turns (turn_id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, client_key TEXT, message TEXT, json TEXT NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS turns_client_key ON turns(conversation_id, client_key) WHERE client_key IS NOT NULL;
CREATE TABLE IF NOT EXISTS operations (operation_id TEXT PRIMARY KEY, approval_id TEXT NOT NULL UNIQUE, conversation_id TEXT NOT NULL, owner TEXT NOT NULL, status TEXT NOT NULL, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS native_approval_requests (operation_id TEXT PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS agent_sessions (conversation_id TEXT NOT NULL, agent TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY (conversation_id, agent));
"""

_ambient: ContextVar[sqlite3.Connection | None] = ContextVar("database_transaction", default=None)


class Rollback(Exception):  # noqa: N818 - a control-flow signal, not an error
    """Raised inside :meth:`Database.atomically` to undo every write made in it."""


class Database:
    def __init__(self, path: Path) -> None:
        self.path = path.resolve()
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.closed = False
        connection = self._connect()
        try:
            connection.executescript(SCHEMA)
        finally:
            connection.close()

    def close(self) -> None:
        """At shutdown: folds the write-ahead log back into the file. No connection outlives a call, so nothing else
        stays open; a later call (a test reading state after shutdown) simply opens a new short-lived connection."""
        connection = self._connect()
        try:
            connection.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        finally:
            connection.close()
        self.closed = True

    def _connect(self) -> sqlite3.Connection:
        connection = sqlite3.connect(self.path, timeout=5, isolation_level=None)  # autocommit unless BEGIN
        connection.execute("PRAGMA journal_mode = WAL")
        return connection

    def execute(self, sql: str, parameters: Sequence[Any] = ()) -> int:
        """Runs a statement (in the current transaction, if any) and returns the number of rows it changed."""
        if (connection := _ambient.get()) is not None:
            return connection.execute(sql, parameters).rowcount
        connection = self._connect()
        try:
            return connection.execute(sql, parameters).rowcount
        finally:
            connection.close()

    def query(self, sql: str, parameters: Sequence[Any] = ()) -> list[tuple[Any, ...]]:
        if (connection := _ambient.get()) is not None:
            return connection.execute(sql, parameters).fetchall()
        connection = self._connect()
        try:
            return connection.execute(sql, parameters).fetchall()
        finally:
            connection.close()

    def next_value(self, name: str, start: int) -> int:
        """Takes and increments a named counter (refund, payment, and ticket numbers) inside the current transaction."""
        with self.atomically():
            self.execute("INSERT INTO counters(name, value) VALUES (?, ?) ON CONFLICT(name) DO NOTHING", (name, start))
            self.execute("UPDATE counters SET value = value + 1 WHERE name = ?", (name,))
            return int(self.query("SELECT value - 1 FROM counters WHERE name = ?", (name,))[0][0])

    @contextmanager
    def atomically(self) -> Generator[None]:
        """Every write inside commits together or none does. Nested use joins the outer transaction."""
        if _ambient.get() is not None:
            yield
            return
        connection = self._connect()
        connection.execute("BEGIN IMMEDIATE")
        token = _ambient.set(connection)
        try:
            yield
        except BaseException:
            connection.execute("ROLLBACK")
            raise
        else:
            connection.execute("COMMIT")
        finally:
            _ambient.reset(token)
            connection.close()
