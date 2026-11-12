"""The local durable store (the Python counterpart of the .NET store): one SQLite file per backend and per run.

The Python backend never shares this file with .NET, and each test or evaluation run gets its own (``STORAGE__PATH``).
Writes inside :meth:`Database.atomically` share one ``BEGIN IMMEDIATE`` transaction on their own connection and
commit together or not at all. Other calls reuse one autocommit connection per thread: opening a connection (and
setting WAL) on every call cost about a quarter of a turn under load, all of it on the event loop. A call is a single
synchronous statement, so coroutines interleaving on the loop never share a transaction. Single process only:
SQLite serializes writers, and guarded updates keep data correct, but per-conversation turn coordination is in-process.
"""

from __future__ import annotations

import sqlite3
import threading
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
        self._local = threading.local()
        self._open: list[sqlite3.Connection] = []
        self._lock = threading.Lock()
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.closed = False
        connection = self._connect()
        try:
            connection.executescript(SCHEMA)
        finally:
            connection.close()

    def close(self) -> None:
        """At shutdown: closes the reused connections and folds the write-ahead log back into the file. A later call
        (a test reading state after shutdown) simply opens a new short-lived connection."""
        self.closed = True
        with self._lock:
            reused, self._open = self._open, []
        for open_connection in reused:
            open_connection.close()
        connection = self._connect()
        try:
            connection.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        finally:
            connection.close()

    def _connect(self, *, shared: bool = False) -> sqlite3.Connection:
        # A reused connection is only ever used by its own thread, but close() may run on another one at shutdown.
        connection = sqlite3.connect(
            self.path, timeout=5, isolation_level=None, check_same_thread=not shared
        )  # autocommit unless BEGIN
        connection.execute("PRAGMA journal_mode = WAL")
        return connection

    def __copy__(self) -> Database:
        return self

    def __deepcopy__(self, memo: dict[int, Any]) -> Database:
        """A handle to one shared store: copying it (as MAF does with workflow state) must not duplicate it."""
        return self

    def _reused(self) -> sqlite3.Connection | None:
        """This thread's autocommit connection, opened on first use; None after :meth:`close`."""
        if self.closed:
            return None
        connection: sqlite3.Connection | None = getattr(self._local, "connection", None)
        if connection is None:
            connection = self._connect(shared=True)
            self._local.connection = connection
            with self._lock:
                self._open.append(connection)
        return connection

    def execute(self, sql: str, parameters: Sequence[Any] = ()) -> int:
        """Runs a statement (in the current transaction, if any) and returns the number of rows it changed."""
        if (connection := _ambient.get() or self._reused()) is not None:
            return connection.execute(sql, parameters).rowcount
        connection = self._connect()
        try:
            return connection.execute(sql, parameters).rowcount
        finally:
            connection.close()

    def query(self, sql: str, parameters: Sequence[Any] = ()) -> list[tuple[Any, ...]]:
        if (connection := _ambient.get() or self._reused()) is not None:
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
