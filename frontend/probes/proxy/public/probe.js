// The browser side of the contract probe (L22). Everything here is relative to the page's own origin: the proxy
// decides where /api/{alias}/... goes. Every server-provided string is rendered with textContent, never as HTML.

const STATE_KEY = "support-probe";
// Recovery keeps polling longer than the backends' longest turn (90 s deadline by default, L15/L42). After that the
// page says so and offers to check again, instead of giving up silently (Codex review on PR #97). ?recoveryMs=…
// shortens the window for the browser tests.
const RECOVERY_MS = Number(new URLSearchParams(location.search).get("recoveryMs") ?? 120_000);
const $ = (id) => document.getElementById(id);

// What the probe has seen, for the browser tests (events in order, per stream).
const trace = { events: [], state: null };
window.__probe = trace;

function load() {
  try {
    return JSON.parse(sessionStorage.getItem(STATE_KEY) ?? "null");
  } catch {
    return null;
  }
}

function save(state) {
  trace.state = state;
  if (state) sessionStorage.setItem(STATE_KEY, JSON.stringify(state));
  else sessionStorage.removeItem(STATE_KEY);
  $("conversation").textContent = state ? `${state.alias} · ${state.conversationId}` : "";
  $("conversation").dataset.alias = state?.alias ?? "";
  // A conversation stays on the backend that created it: the selector is locked while one is open.
  $("backend").disabled = Boolean(state);
}

// The conversation's backend is part of every URL; nothing else chooses it.
const api = (path, alias = load()?.alias) => `/api/${alias}/v1${path}`;

function status(text) {
  $("status").textContent = text ?? "";
}

function showError(text) {
  $("error").textContent = text ?? "";
}

function entry(role, text = "") {
  const element = document.createElement("div");
  element.className = "entry";
  element.dataset.role = role;
  element.textContent = text;
  $("transcript").append(element);
  return element;
}

/** Shows a problem response safely: its title and detail as text, never the raw body. */
async function problemText(response) {
  try {
    const body = await response.json();
    return `${body.title ?? "Error"}: ${body.detail ?? body.code ?? response.status}`;
  } catch {
    return `The request failed (${response.status}).`;
  }
}

/** Renders a finished turn (from a stream's final event, a JSON response, or a recovery lookup). */
function renderResult(result, bubble) {
  const target = bubble ?? entry("assistant");
  target.dataset.outcome = result.outcome;
  target.dataset.turnId = result.turn_id;
  if (result.outcome === "failed") {
    target.textContent = "";
    target.remove();
    showError(result.error?.detail ?? "Something went wrong.");
  } else if (result.outcome === "cancelled") {
    target.textContent = "(stopped)";
  } else {
    target.textContent = result.reply?.text ?? "";
    target.dataset.kind = result.reply?.kind ?? "";
  }
  status(result.outcome === "awaiting_approval" ? "Waiting for your decision." : "");
}

/** Reads a Server-Sent Events body with fetch's stream reader (POST, so EventSource doesn't apply). */
async function readStream(response, bubble, { abortAfterStart, controller } = {}) {
  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = "";
  let streamed = "";
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += value.replaceAll("\r\n", "\n");
    let boundary;
    while ((boundary = buffer.indexOf("\n\n")) >= 0) {
      const block = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);
      const data = block
        .split("\n")
        .filter((line) => line.startsWith("data: "))
        .map((line) => line.slice(6))
        .join("\n");
      if (!data) continue; // a ": keepalive" comment
      const event = JSON.parse(data);
      trace.events.push(event);
      switch (event.type) {
        case "turn.started":
          save({ ...load(), lastTurnId: event.turn_id });
          if (abortAfterStart) {
            controller.abort(); // a dropped connection: the turn runs on and is recovered by its id
            status("Disconnected.");
            return null;
          }
          break;
        case "turn.progress":
          status(`Working: ${event.data.stage}`);
          break;
        case "reply.delta":
          streamed += event.data.text;
          bubble.textContent = streamed;
          break;
        case "reply.reset":
          streamed = "";
          bubble.textContent = "";
          break;
        case "turn.final":
          renderResult(event.data.result, bubble);
          return event.data.result;
      }
    }
  }
  throw new Error("The stream ended without a final event.");
}

async function streamed(path, body, options = {}) {
  const controller = new AbortController();
  const response = await fetch(api(path), {
    method: "POST",
    headers: { "content-type": "application/json", accept: "text/event-stream" },
    body: JSON.stringify(body),
    signal: controller.signal,
  });
  if (!response.ok) {
    showError(await problemText(response));
    return null;
  }
  const bubble = entry("assistant");
  try {
    return await readStream(response, bubble, { ...options, controller });
  } catch (error) {
    if (error.name === "AbortError") return null;
    showError("The connection was interrupted. Refresh to see what happened.");
    return null;
  }
}

async function ensureConversation() {
  const current = load();
  if (current?.conversationId) return current;
  const alias = $("backend").value;
  const response = await fetch(api("/conversations", alias), {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: "{}",
  });
  if (!response.ok) {
    showError(await problemText(response));
    return null;
  }
  const conversation = await response.json();
  const state = { alias, conversationId: conversation.conversation_id };
  save(state);
  return state;
}

export async function send(message, options = {}) {
  showError("");
  const state = await ensureConversation();
  if (!state) return null;
  entry("user", message);
  const result = await streamed(`/conversations/${state.conversationId}/turns`, { client_turn_id: crypto.randomUUID(), message }, options);
  await refreshPending();
  return result;
}

export async function decide(approval, decision) {
  showError("");
  const state = load();
  const result = await streamed(`/conversations/${state.conversationId}/approvals/${approval.approval_id}`, {
    operation_id: approval.operation_id,
    decision,
  });
  await refreshPending();
  return result;
}

/** The refresh path: the conversation itself says what is pending and whether a turn is still running. */
export async function refreshPending() {
  const state = load();
  const list = $("pending");
  list.replaceChildren();
  if (!state?.conversationId) return null;
  const response = await fetch(api(`/conversations/${state.conversationId}`));
  if (!response.ok) {
    showError(await problemText(response));
    if (response.status === 404) save(null); // gone, or not this customer's: start again
    return null;
  }
  const conversation = await response.json();
  for (const approval of conversation.pending_approvals) {
    const item = document.createElement("li");
    item.dataset.approvalId = approval.approval_id;
    const summary = document.createElement("span");
    summary.textContent = `${approval.summary} `;
    const approve = document.createElement("button");
    approve.textContent = "Approve";
    approve.dataset.testid = "approve";
    approve.onclick = () => decide(approval, "approve");
    const reject = document.createElement("button");
    reject.textContent = "Reject";
    reject.dataset.testid = "reject";
    reject.onclick = () => decide(approval, "reject");
    item.append(summary, approve, reject);
    list.append(item);
  }
  return conversation;
}

/**
 * After a refresh or a dropped stream: find the turn, wait while it runs, then show it. The conversation's own
 * active_turn_id comes first: a reload can happen before turn.started arrived, when the browser has no id of its own
 * for the running turn (Codex review on PR #97).
 */
export async function recover(activeTurnId) {
  const state = load();
  const turnId = activeTurnId ?? state?.lastTurnId;
  if (!state?.conversationId || !turnId) return null;
  if (turnId !== state.lastTurnId) save({ ...state, lastTurnId: turnId });
  $("retry")?.remove();
  const deadline = Date.now() + RECOVERY_MS;
  let wait = 250;
  for (;;) {
    const response = await fetch(api(`/conversations/${state.conversationId}/turns/${turnId}`));
    if (!response.ok) {
      showError(await problemText(response));
      return null;
    }
    const turn = await response.json();
    if (turn.state === "finished") {
      const bubble = entry("assistant");
      bubble.dataset.recovered = "true";
      renderResult(turn, bubble);
      return turn;
    }
    if (Date.now() >= deadline) {
      // An explicit state, not a silent stop: the turn may still finish, and the customer can check again.
      status("Still working. It is taking longer than usual.");
      const retry = document.createElement("button");
      retry.id = "retry";
      retry.dataset.testid = "retry";
      retry.textContent = "Check again";
      retry.onclick = async () => {
        if (await recover(turnId)) await refreshPending();
      };
      $("status").after(retry);
      return null;
    }
    status("Still working…");
    await new Promise((resolve) => setTimeout(resolve, wait));
    wait = Math.min(wait * 2, 2_000);
  }
}

async function start() {
  const response = await fetch("/api/backends");
  const { backends } = await response.json();
  for (const alias of backends) {
    const option = document.createElement("option");
    option.value = option.textContent = alias;
    $("backend").append(option);
  }
  const state = load();
  if (state?.alias) $("backend").value = state.alias;
  save(state);
  $("new").onclick = () => {
    save(null);
    $("transcript").replaceChildren();
    $("pending").replaceChildren();
    showError("");
    status("");
  };
  $("composer").onsubmit = (event) => {
    event.preventDefault();
    const message = $("message").value.trim();
    if (!message) return;
    $("message").value = "";
    send(message);
  };
  const conversation = await refreshPending();
  if (conversation && (await recover(conversation.active_turn_id ?? undefined))) {
    // The recovered turn may have just finished as awaiting_approval: its approval only appears once the turn is
    // stored, so read the list again (Codex review on PR #97).
    await refreshPending();
  }
  document.body.dataset.ready = "true";
}

window.__probeApi = { send, decide, refreshPending, recover };
start();
