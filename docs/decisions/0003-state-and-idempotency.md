# 0003 - Durable state and idempotent writes

- Status: Accepted (2026-11-10); the Python port was added (2026-11-10). Assistant-drafted; the owner reviews it with each PR.
- Applies to: `src/dotnet/Support.Api` and `src/python/support_agent` (see "The Python backend" below). Each backend has its own store and file.

## Context

Before this decision everything lived in process memory:

- conversations and their finished turns;
- operations and approvals;
- the paused native approval requests (`ToolApprovalRequestContent`);
- the specialists' `AgentSession`s;
- the simulated business state (cancelled orders, refunds, tickets).

A restart between "please cancel" and "approve" lost the approval, so resuming reported "nothing has been changed". Within one process, a write ran at most once because of an in-memory flag. But the business change and the operation's "committed" status were two separate steps, and nothing durable stopped a retry after a crash from applying the write again.

## Decision

### 1. Store: one local SQLite file per backend and per run

- **Microsoft.Data.Sqlite 10.0.15**, a single file at `Storage:Path`. The default is `.data/dotnet/support.db` under the content root, and `.data/` is ignored by Git. Tests give each host its own temporary file.
- **Why SQLite:**
  - it's a real ACID store, with transactions, unique keys and guarded updates;
  - it's in-process, with no server or container to run;
  - it's supported on every platform the lab targets;
  - one file per run is easy to isolate and delete.
- **What we didn't choose:**
  - *A JSON file:* no atomic multi-record updates.
  - *Postgres or Redis:* infrastructure no ticket needs yet.
  - *EF Core:* six small tables don't justify an ORM and its migrations. The SQL is short and kept in each module's Infrastructure folder.
- **Isolation:** the .NET backend, the Python backend and each evaluation run use **separate files**. Mutable state is never shared between backends (out of scope by the ticket). An evaluation run points `Storage:Path` at a fresh file.

### 2. What survives a restart

| State | Where | Restored by |
| --- | --- | --- |
| Conversation: owner, verified context | `conversations` | `SqliteConversationStore` (only through `FindOwned`, so only its owner) |
| Finished turns, with the key they were claimed with (`client_turn_id` or `approval:{id}`) | `turns` (unique per conversation and key) | the same; a repeat replays the stored turn |
| Operations: binding, exact arguments, expiry, status, decision, result, failure | `operations` | `SqliteOperationStore`; the status endpoint still checks the owner |
| Paused native approval request (MAF/M.E.AI JSON form) | `native_approval_requests` | `PendingNativeApprovals` |
| Each specialist's `AgentSession` per conversation | `agent_sessions` | `ConversationSessions` (`SerializeSessionAsync` / `DeserializeSessionAsync`) |
| Simulated business state: changed orders, refund payments, tickets, id counters | `changed_orders`, `payments`, `tickets`, `counters` | the module repositories (fixture files are never changed) |

**Stable identities:**
- Agents have fixed ids (`orders`, `billing`, `technical`, `triage`), so a stored session belongs to the same agent after a restart.
- The workflow name is fixed (`support-routing`).
- Conversation, turn, operation and approval ids are random and stored.

A turn that was **still running** when the process stopped is not stored. It's as if it never happened, and the client retries it with the same `client_turn_id`.

### 3. Sessions, not workflow checkpoints

MAF workflow checkpoints save a run's state at superstep boundaries, so a paused or interrupted *workflow run* can be resumed. **We don't use them.** Our workflow never pauses mid-run:

- every turn runs triage, then one executor, then the reply, to completion;
- an approval *ends* the turn (`awaiting_approval`).

The paused state that must survive is:
- the specialist's **agent session** (its history),
- the stored **native approval request**, and
- the **operation record**.

The decision resumes them as a new turn (`ResumeAsync`). Checkpoints would add a second copy of state with nothing to restore. If a later ticket makes the workflow itself wait (for example, human-in-the-loop *inside* a run), that is when checkpoints become the right tool.

### 4. One write, applied at most once

- **The operation id is the durable key.** Each write runs only through `OperationService.ExecuteApprovedAsync`, inside an approved `ApprovalScope` for that operation.
- **Atomic:** the module's business change (cancel, refund plus payment, ticket) and `operations.status = 'committed'` are written in **one `BEGIN IMMEDIATE` transaction** (`IUnitOfWork.AtomicallyAsync`). Either both are stored or neither is.
- **Guarded:** every status change is `UPDATE … WHERE operation_id = @id AND status = @expected`.
  - If another run (a duplicate, a retry, or another host after a restart) finished the operation first, the update changes no row. The whole transaction rolls back, including the business change, and the stored result is reported instead.
  - The same guard records a decision only once, and stops a late failure from overwriting a commit.
- **Records that belong together are one transaction:**
  - the operation and its paused native request, so an approval is never listed without the request it resumes;
  - a finished turn and the context it produced (Codex review on PR #77).
- **An approval is actionable only once the turn that asked for it is stored.** A proposal left by a turn that never finished (the process stopped in between) isn't listed, can't be decided (`410`), and is closed as `expired` by the next turn. So a retried submission can't leave two approvals for one request.
- **A decision whose turn fails to save releases its reservation.** The retry answers from the operation record.
- **Turns:** the first stored turn for a key wins. A host that was stuck mid-turn while a restarted host answered the retry gets the stored turn back, rather than writing a second one.
- **Retried decisions after a crash:**
  - The decision was recorded but its turn was never stored, and the operation is already `committed`, `failed` or `rejected` → a factual turn is built **from the record**. No model call, no write.
  - The operation is still `running` → nothing was applied (atomicity), so the stored native request is resumed. The guarded commit makes this safe even if another run is racing.

### 5. What this does *not* guarantee

- **Atomic locally is not exactly-once against an external provider.** Our "write" is a local simulation, so it can share a transaction with its operation record. A real refund calls a payment provider over the network, outside our transaction. If the process dies after the provider charged but before our commit, our record says `running`, and resuming would call the provider again.
- **What exactly-once needs there:**
  - the provider must support an **idempotency key**, and we'd send the operation id;
  - or a two-step design: record the intent → call → record the outcome, plus reconciliation (ask the provider what happened before retrying).
  - That's out of scope for this ticket. The operation id is already the right key to send.
- **Other limits:**
  - Only one process should serve one file in production. SQLite serializes writers, and the guards keep data correct across processes, but the in-process "one turn at a time" lock is per process. Two processes on one file could each run a turn of the same conversation. The tests use two hosts on one file only to simulate a restart.
  - Expiry is still lazy: it's applied when an approval is touched.
  - There are no retention or cleanup rules yet.

## The Python backend

The same design and guarantees, ported idiomatically. The observable behaviour is the same, and the durable-state test suite is ported case by case (`tests/test_durable_state.py`).

- **Store:** the standard library's `sqlite3`, so no new dependency. The file is `STORAGE__PATH`, by default `.data/python/support.db` (ignored by Git), never the .NET file. Tests give each host its own temporary file, and the DevUI process uses its own `devui.db`.
- **Same tables, same rules** (`database.py`):
  - `Database.atomically()` is one `BEGIN IMMEDIATE` transaction. Nested use joins it, through a `ContextVar` holding the connection.
  - Other calls reuse one autocommit connection per thread (each call is one synchronous statement, so coroutines never share a transaction). This doubled throughput under load ([load results](../evaluations/load.md#the-improvement-reused-sqlite-connections-python)). At shutdown, the lifespan closes them and checkpoints the write-ahead log (`Resources` owns the database).
- **Mapping:**

  | .NET | Python |
  | --- | --- |
  | `IUnitOfWork.AtomicallyAsync` | `Database.atomically()` |
  | `IOperationStore.TryUpdate` | `OperationStore.try_update` (reloads the stored record on conflict) |
  | `PendingNativeApprovals` (`ToolApprovalRequestContent` JSON) | `native_approval_requests` with `Content.to_dict()` / `Content.from_dict()` of the `function_approval_request` |
  | `SerializeSessionAsync` / `DeserializeSessionAsync` | `AgentSession.to_dict()` / `AgentSession.from_dict()` (the bounded history lives in `state["in_memory"]`) |
  | Module repositories over `changed_orders`, `payments`, `tickets`, `counters` | `SimulatedState` over the same tables; fixture files are never changed |

- **Verified against MAF Python 1.25.0:**
  - a session round-trips through JSON with its history;
  - a restored `function_approval_request` still produces `to_function_approval_response(approved=…)`;
  - the resumed run after a restart commits once (tested for all three writes, and live with Claude Haiku).
- **Python-specific detail:** MAF Python runs an approved tool on the event loop, inside `approval_scope`. `execute_approved` re-reads the stored status inside the transaction before writing. A run that is stale (the operation was already finished) writes nothing and reports the stored result. If the guarded commit still loses, the business change rolls back with it (`Rollback`).
- **Same limits** as section 5. Within one process, at most one turn per conversation runs at a time; across processes, only the stored guards apply. A test with two hosts on one file approving at once shows that the guards alone still give one write and one stored turn.

## Connection pooling

The .NET store originally opened a fresh connection for every call (`Pooling = false`). Under concurrent writes, the load scenarios showed 150–190 ms tail latencies. With pooling, the write-race p95 dropped to 17–35 ms and read throughput roughly doubled, with every correctness check unchanged. Transactions are unaffected: a write still runs in one `BEGIN IMMEDIATE` transaction on one connection, and each pooled connection re-applies the pragmas when opened. Evidence: [load results](../evaluations/load.md#the-improvement-pooled-sqlite-connections-net).

## Consequences

- A restart between request and approval loses nothing. Wrong owner, mismatch, rejection and expiry behave exactly as before the restart.
- Duplicates (concurrent or after a restart) and a crash right after the commit produce **one** mutation and a retrievable final status.
- Each turn costs a few small SQLite writes (the turn, the context, the session), which is negligible next to a model call.
