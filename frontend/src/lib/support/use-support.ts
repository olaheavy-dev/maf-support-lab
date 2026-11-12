"use client";

// Drives one support conversation: creates it on the first message, streams each turn into the reducer,
// and recovers when a stream ends early. Recovery uses only what the contract makes safe: re-sending the
// same client_turn_id returns the original turn, repeating the same decision is idempotent, and
// GET …/turns/{id} and GET /operations/{id} are authoritative. The transcript and approvals are kept in
// this tab's sessionStorage, so a reload picks the conversation up again.
import { useCallback, useEffect, useLayoutEffect, useReducer, useRef, useState } from "react";

import {
  cancelTurn,
  createConversation,
  getConversation,
  getOperation,
  getTurn,
  streamDecision,
  streamTurn,
  SupportApiError,
} from "./client";
import {
  initialState,
  reduce,
  type Action,
  type ApprovalState,
  type Entry,
  type OperationStatus,
  type State,
} from "./conversation";
import type { Approval, StreamEvent } from "./types";

const STORAGE_KEY = "voltcart-conversation";
const POLL_ATTEMPTS = 60;
const POLL_INTERVAL_MS = 2000;

type Saved = {
  conversationId: string;
  entries: Entry[];
  approvals: Record<string, ApprovalState>;
  operations: Record<string, OperationStatus>;
};

type Dispatch = (action: Action) => void;

export function useSupport() {
  const [state, dispatch] = useReducer(reduce, initialState);
  // `busy` is set the moment a request starts and cleared when it is fully done, so a second click before
  // React re-renders can't send twice (one turn at a time per conversation). The UI reads the state copy.
  const [busy, setBusyState] = useState(true); // true until any saved conversation is restored
  const busyRef = useRef(true);
  const stateRef = useRef<State>(state);
  useLayoutEffect(() => {
    stateRef.current = state;
  });
  const setBusy = useCallback((value: boolean) => {
    busyRef.current = value;
    setBusyState(value);
  }, []);

  // Restore this tab's conversation after a reload. Nothing can be sent until this settles: if the server
  // can't be reached, the page offers to try again (or to start over) rather than starting a new
  // conversation over the saved one.
  const [restoreFailed, setRestoreFailed] = useState(false);
  const restore = useCallback(async () => {
    const saved = load();
    if (!saved) {
      setBusy(false);
      return;
    }
    setBusy(true);
    setRestoreFailed(false);
    try {
      const conversation = await getConversation(saved.conversationId);
      // An entry that was mid-turn is kept if it has a turn id: its result is fetched below. One without
      // a turn id never reached the server's turn, so there is nothing to recover.
      const unfinished = (entry: Entry) =>
        entry.kind === "assistant" && (entry.status === "waiting" || entry.status === "streaming");
      const entries = saved.entries.filter(
        (entry) => !unfinished(entry) || (entry.kind === "assistant" && entry.turnId),
      );
      dispatch({
        type: "restored",
        conversation,
        entries,
        approvals: saved.approvals ?? {},
        operations: saved.operations ?? {},
      });
      const pending = new Set(conversation.pending_approvals.map((each) => each.approval_id));
      for (const [approvalId, approval] of Object.entries(saved.approvals ?? {})) {
        if (!pending.has(approvalId)) await lookUp(dispatch, approval.operationId, approvalId);
      }
      const recovering = entries.filter(unfinished) as Extract<Entry, { kind: "assistant" }>[];
      for (const entry of recovering) {
        // Active while it recovers, so the composer offers Stop for a reply that is still running.
        dispatch({ type: "recovering", assistantId: entry.id });
        await poll(dispatch, entry.id, conversation.conversation_id, entry.turnId!);
      }
      const active = conversation.active_turn_id;
      if (active && !recovering.some((entry) => entry.turnId === active)) {
        const assistantId = id();
        dispatch({ type: "resuming", assistantId, turnId: active });
        await poll(dispatch, assistantId, conversation.conversation_id, active);
      }
      setBusy(false);
    } catch (error) {
      if (error instanceof SupportApiError && error.code === "not_found") {
        // The server says it's gone: forget it and start fresh.
        clear();
        setBusy(false);
      } else {
        // Briefly unreachable: keep the saved conversation as the one to continue.
        setRestoreFailed(true);
      }
    }
  }, [setBusy]);

  useEffect(() => {
    // Syncing with external systems (this tab's storage and the server) once on mount, which is what effects
    // are for; with nothing saved, restore() sets `busy` false straight away.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    void restore();
  }, [restore]);

  useEffect(() => {
    // Saved as soon as the conversation exists, mid-turn too, so a reload during the first reply can still
    // find it and resume the server's active turn. Restore drops entries that hadn't finished.
    if (state.conversation) {
      save({
        conversationId: state.conversation.conversation_id,
        entries: state.entries,
        approvals: state.approvals,
        operations: state.operations,
      });
    }
  }, [state.conversation, state.entries, state.approvals, state.operations]);

  /**
   * Streams one turn. If the stream ends before turn.final: with a turn id, poll that turn; without one,
   * send the very same request once more (safe: the contract makes both kinds idempotent), then give up
   * honestly.
   */
  const run = useCallback(
    async (
      assistantId: string,
      events: () => AsyncGenerator<StreamEvent>,
      conversationId: string,
      approval?: Approval,
    ) => {
      let turnId: string | undefined;
      for (let attempt = 0; attempt < 2; attempt++) {
        let finished = false;
        try {
          for await (const event of events()) {
            turnId = event.turn_id;
            if (event.type === "turn.final") finished = true;
            dispatch({ type: "event", assistantId, event });
          }
          if (finished) return;
        } catch (error) {
          const refused = error instanceof SupportApiError && error.code !== "unreachable";
          if (refused && !turnId) {
            await refuse(dispatch, assistantId, error, approval);
            return;
          }
        }
        if (turnId) {
          await poll(dispatch, assistantId, conversationId, turnId);
          return;
        }
      }
      dispatch({
        type: "unknown",
        assistantId,
        noticeId: id(),
        message: approval
          ? "The connection dropped before your decision was confirmed. Its status is shown on the card once the server answers."
          : "The connection dropped before a reply arrived, so we can't confirm what happened. Ask again to check.",
      });
      if (approval) await lookUp(dispatch, approval.operation_id, approval.approval_id);
    },
    [],
  );

  /** Sends a message. Returns false if it can't be sent now, so the composer keeps the text. */
  const send = useCallback(
    (text: string): boolean => {
      const message = text.trim();
      if (!message || busyRef.current) return false;
      setBusy(true);
      void (async () => {
        const assistantId = id();
        dispatch({ type: "sent", customerId: id(), text: message, assistantId });
        try {
          let conversation = stateRef.current.conversation;
          if (!conversation) {
            try {
              conversation = await createConversation();
              dispatch({ type: "conversation", conversation });
            } catch (error) {
              dispatch({ type: "refused", assistantId, noticeId: id(), message: explain(error) });
              return;
            }
          }
          const conversationId = conversation.conversation_id;
          // One client_turn_id per message: a retry of this request returns the same turn, never a new one.
          const body = { client_turn_id: crypto.randomUUID(), message };
          await run(assistantId, () => streamTurn(conversationId, body), conversationId);
        } finally {
          setBusy(false);
        }
      })();
      return true;
    },
    [run, setBusy],
  );

  const decide = useCallback(
    (approval: Approval, decision: "approve" | "reject") => {
      const conversation = stateRef.current.conversation;
      if (!conversation || busyRef.current) return;
      setBusy(true);
      void (async () => {
        const assistantId = id();
        dispatch({ type: "deciding", approval, decision, assistantId });
        const conversationId = conversation.conversation_id;
        const body = { operation_id: approval.operation_id, decision };
        try {
          await run(
            assistantId,
            () => streamDecision(conversationId, approval.approval_id, body),
            conversationId,
            approval,
          );
        } finally {
          setBusy(false);
        }
      })();
    },
    [run, setBusy],
  );

  /** Asks the running turn to stop. Cooperative: the turn's own final event says what happened. */
  const stop = useCallback(async (): Promise<boolean> => {
    const { conversation, activeId, entries } = stateRef.current;
    const active = entries.find((entry) => entry.id === activeId);
    if (!conversation || !active || active.kind !== "assistant" || !active.turnId) return false;
    try {
      await cancelTurn(conversation.conversation_id, active.turnId);
      return true;
    } catch (error) {
      // The turn carries on and still ends with its own result; just say the stop didn't go through.
      dispatch({
        type: "notice",
        noticeId: id(),
        message: `Couldn't stop the reply. ${explain(error)}`,
      });
      return false;
    }
  }, []);

  /** Starts a new conversation. Also the way out when a saved one can't be restored. */
  const startOver = useCallback(() => {
    if (busyRef.current && !restoreFailed) return;
    clear();
    dispatch({ type: "reset" });
    setRestoreFailed(false);
    setBusy(false);
  }, [restoreFailed, setBusy]);

  return { state, busy, send, decide, stop, startOver, restoreFailed, retryRestore: restore };
}

/** A refused request: say why, and settle what it means for the approval it was about. */
async function refuse(
  dispatch: Dispatch,
  assistantId: string,
  error: SupportApiError,
  approval?: Approval,
) {
  const base = { type: "refused" as const, assistantId, noticeId: id(), message: explain(error) };
  if (!approval) return dispatch(base);
  if (error.code === "approval_expired") {
    return dispatch({ ...base, approvalId: approval.approval_id, approvalStatus: "expired" });
  }
  dispatch({ ...base, approvalId: approval.approval_id, approvalStatus: "pending" });
  if (error.code === "approval_already_decided") {
    await lookUp(dispatch, approval.operation_id, approval.approval_id);
  }
}

/** Polls the authoritative turn while it still runs; gives up honestly after about two minutes. */
async function poll(
  dispatch: Dispatch,
  assistantId: string,
  conversationId: string,
  turnId: string,
) {
  for (let attempt = 0; attempt < POLL_ATTEMPTS; attempt++) {
    try {
      const result = await getTurn(conversationId, turnId);
      dispatch({ type: "polled", assistantId, result });
      if (result.state === "finished") return;
    } catch {
      break;
    }
    await new Promise((resolve) => setTimeout(resolve, POLL_INTERVAL_MS));
  }
  dispatch({
    type: "unknown",
    assistantId,
    noticeId: id(),
    message: "We couldn't confirm how this reply ended. Ask again to check where things stand.",
  });
}

/** Reads an operation's authoritative status; leaves things as they are if the server can't be asked. */
async function lookUp(dispatch: Dispatch, operationId: string, approvalId: string) {
  try {
    const operation = await getOperation(operationId);
    dispatch({ type: "operation", operationId, status: operation.status, approvalId });
  } catch {
    // Unknown for now: the card keeps what it knew.
  }
}

/** A refused request in the customer's words: what happened and what to do. */
export function explain(error: unknown): string {
  if (!(error instanceof SupportApiError))
    return "Something went wrong in this page. Reload it and try again.";
  switch (error.code) {
    case "turn_in_progress":
      return "A reply is still on its way. Wait for it, or stop it first.";
    case "approval_expired":
      return "That approval expired, so nothing was changed. Ask again for a new one.";
    case "approval_already_decided":
      return "That was already decided. Its status is shown on the card.";
    case "not_found":
      return "This conversation isn't available any more. Start a new one.";
    case "rate_limited":
      return "Too many requests at once. Wait a moment, then try again.";
    default:
      return error.message;
  }
}

function id(): string {
  return crypto.randomUUID();
}

function load(): Saved | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as Saved) : null;
  } catch {
    return null;
  }
}

function save(saved: Saved): void {
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(saved));
  } catch {
    // Storage blocked or full: the conversation still works, it just won't survive a reload.
  }
}

function clear(): void {
  try {
    sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // Nothing to clear.
  }
}
