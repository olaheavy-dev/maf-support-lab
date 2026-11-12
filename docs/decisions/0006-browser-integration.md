# 0006 - Browser integration with the version-1 contract

- Status: **Superseded** (2026-11-11). Accepted, then retired the same day when the owner dropped the frontend: the browser probes, their CI job, and the AppHost resource were removed. Kept as a record of what was verified; nothing in the repository implements it now. A new demo frontend came back on 2026-11-12 under [0007](0007-demo-frontend.md). The contract is verified by the cross-backend probes.
- Versions: Node 26.13.1 (LTS "Lithium"; 26.13.0 used locally), `@playwright/test` 1.65.0 with Chromium (headless shell 158), and `Aspire.Hosting.JavaScript` 13.6.5, with every Aspire pin raised to 13.6.5 for advisory GHSA-f7vh-h5g3-7jrh.

## Decision

The version-1 contract works from a browser **unchanged**. The probes found no breaking change, so the schemas and adapters stay as they are. The browser-side rules, chosen by the owner, are:

1. **Relative URLs only.**
   - The page calls `/api/{alias}/v1/...` on its own origin. A server-side proxy maps the alias to a backend.
   - The browser never sees a backend address, an Aspire service name, or a provider key; a test scans the browser files.
2. **The alias is server-controlled.**
   - The proxy has a fixed allowlist (`dotnet`, `python`) and gets each address from Aspire's `services__<resource>__http__0`, or `__https__0` when that's all there is.
   - An unknown alias, an encoded URL in its place, or a path with `..` gets a contract `404`. An unreachable backend gets `503 service_unavailable`.
3. **A conversation stays on its backend.**
   - The browser chooses an alias once, when it creates the conversation, and keeps `{alias, conversation_id}` in `sessionStorage`. Every later call carries that alias, and the backend picker is locked while a conversation is open.
   - Sending the id to the other alias gets that backend's own `404`, because the backends share nothing, so nothing crosses over.
   - This needs no proxy state and no contract change. (The alternative, the proxy remembering id → backend, would need durable, shared proxy storage.)
4. **Streaming uses `fetch` and a stream reader.** `EventSource` can't POST. The page parses SSE blocks, skips `: keepalive` comments, applies `reply.reset`, and treats `turn.final` as authoritative. The proxy pipes responses chunk by chunk, with no buffering.
5. **What the browser needs to render a paused workflow** is all in the contract:
   - the turn's `outcome: awaiting_approval` and `pending_approval`;
   - after a refresh, `GET /conversations/{id}`, whose `pending_approvals` (with `summary`) and `active_turn_id` say what is waiting and what is still running;
   - the decision then resumes the work as a new turn.
6. **Recovering from a disconnect:**
   - the browser stores `turn.started`'s `turn_id`. A dropped connection is not a cancellation, so after a refresh the browser polls `GET …/turns/{turn_id}` until `state: finished`, then shows the stored result.
   - Deltas are not replayed; the stored turn is the truth.
7. **Safe rendering:**
   - every server or customer string is set with `textContent`;
   - problems show only `title: detail`;
   - a failed turn shows only `error.detail`, the contract's fixed, safe text.
8. **Aspire:**
   - the probe proxy is an AppHost resource (`browser-probe`, `AddNodeApp`, no npm install because the proxy has no packages), with `WithReference` to both backends.
   - Aspire serves the Python backend over https with its development certificate, and sets `NODE_EXTRA_CA_CERTS`, so Node trusts it.
   - The proxy sends each backend its own loopback `Host`, which .NET's Development DevUI guard requires on `/api/v1/conversations`.

## How a refreshed browser discovers a pending approval (the stretch)

1. On load, read `{alias, conversationId, lastTurnId}` from `sessionStorage`. With no stored conversation there is nothing to discover. Pending approvals are never searched across conversations or backends.
2. `GET /api/{alias}/v1/conversations/{conversationId}`.
   - A `404` (expired, deleted, or not this customer's) clears the stored state, and the browser starts over.
   - A `200` gives `pending_approvals[]`, which are the server's truth, with `summary` and `expires_at`. Render each with Approve and Reject.
3. Recover `active_turn_id` if set (a reload can come before `turn.started` gave the browser an id), otherwise the stored `lastTurnId`. Poll `GET …/turns/{id}` with backoff for longer than the backends' 90 s turn deadline (120 s), then render it, and re-read the conversation so an approval from that turn appears. If the window passes, say so and offer **Check again**: never stop silently.
4. A decision posts `{operation_id, decision}` to `…/approvals/{approval_id}`.
   - A decision repeated after a lost response returns the same turn.
   - A late one gets `410 approval_expired`, and a conflicting one gets `409`. Both are rendered as text.
5. After any decision, re-read the conversation, so the list matches the server.

## Evidence

- **Playwright:** 17 tests, the same 7 per backend plus 3 shared, against both real backends in fake mode, through the proxy.
  - **Per backend:** streamed chat; approve; reject; refresh rediscovery; a dropped stream recovered; safe errors and markup; binding.
  - **Shared:** a scan of the browser files for addresses, service names and keys; the alias list; how addresses are read from the environment.
- **Mutations caught:** `innerHTML` for user text; no recovery on load; the proxy falling back to a default backend.
- **Through Aspire:** the proxy listed both aliases, created a conversation on each backend, and returned `404` for each id under the other alias. I didn't send model turns through Aspire, because the owner's .NET user-secrets select Live mode.

## Not decided here

- **The product UI** and its framework, and authentication (out of scope).
- **The proxy itself** is a disposable probe. A real frontend would have had to decide whether its server or dev proxy follows the same rules.
