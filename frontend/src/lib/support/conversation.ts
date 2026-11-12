// The chat page's state: the transcript, how each turn ran, and what became of each approval. A pure
// reducer over the contract's stream events and results, so the page's behaviour is testable without a
// browser or a backend. What changed is always read from operation status, the authoritative record
// (contracts/README.md, Approvals and operations), never inferred from progress events.
import type {
  Approval,
  Conversation,
  OperationSummary,
  ProgressStage,
  Route,
  StreamEvent,
  TurnResult,
} from "./types";

/** One step of the circuit a turn runs through, as the server reports it. */
export type Step =
  | { kind: "triage" }
  | { kind: "route"; route: Route }
  | { kind: "specialist" }
  | { kind: "tool"; name: string }
  | { kind: "gate"; stage: Extract<ProgressStage, "awaiting_approval" | "executing_operation"> }
  | { kind: "reply" };

export type CustomerEntry = { kind: "customer"; id: string; text: string };

export type AssistantEntry = {
  kind: "assistant";
  id: string;
  turnId?: string;
  /** For a turn resumed by an approval decision: which approval, and what the customer chose. */
  approvalId?: string;
  decision?: "approve" | "reject";
  /** `unknown`: the connection ended and the server couldn't be asked what happened. */
  status: "waiting" | "streaming" | "finished" | "unknown";
  text: string;
  steps: Step[];
  result?: TurnResult;
};

export type NoticeEntry = { kind: "notice"; id: string; text: string };

export type Entry = CustomerEntry | AssistantEntry | NoticeEntry;

export type OperationStatus = OperationSummary["status"];

/** An approval as the page knows it. Its outcome is the operation's status, kept separately. */
export type ApprovalState = {
  operationId: string;
  status: "pending" | "deciding" | "decided" | "expired";
  choice?: "approve" | "reject";
};

export type State = {
  conversation?: Conversation;
  entries: Entry[];
  /** The entry receiving a turn right now, if any: one turn at a time per conversation. */
  activeId?: string;
  approvals: Record<string, ApprovalState>;
  operations: Record<string, OperationStatus>;
};

export const initialState: State = { entries: [], approvals: {}, operations: {} };

export type Action =
  | { type: "conversation"; conversation: Conversation }
  | { type: "sent"; customerId: string; text: string; assistantId: string }
  | { type: "deciding"; approval: Approval; decision: "approve" | "reject"; assistantId: string }
  | { type: "resuming"; assistantId: string; turnId: string }
  /** A saved entry whose turn is being recovered after a reload: it is the running one meanwhile. */
  | { type: "recovering"; assistantId: string }
  | { type: "event"; assistantId: string; event: StreamEvent }
  /** The server's current view of a turn, after the stream ended early. */
  | { type: "polled"; assistantId: string; result: TurnResult }
  /** Refused before the turn started (a problem response): the placeholder becomes a notice. */
  | {
      type: "refused";
      assistantId: string;
      noticeId: string;
      message: string;
      approvalId?: string;
      /** What the refusal means for the approval: still open, or expired. */
      approvalStatus?: "pending" | "expired";
    }
  /** The stream ended and the server couldn't confirm what happened. */
  | { type: "unknown"; assistantId: string; noticeId: string; message: string }
  /** An operation's authoritative status, looked up directly. */
  | { type: "operation"; operationId: string; status: OperationStatus; approvalId?: string }
  | { type: "notice"; noticeId: string; message: string }
  | {
      type: "restored";
      conversation: Conversation;
      entries: Entry[];
      approvals: Record<string, ApprovalState>;
      operations: Record<string, OperationStatus>;
    }
  | { type: "reset" };

export function reduce(state: State, action: Action): State {
  switch (action.type) {
    case "conversation":
      return { ...state, conversation: action.conversation };
    case "reset":
      return initialState;
    case "restored": {
      // The server's pending approvals are the truth: open exactly those. A saved approval that is no
      // longer pending has been decided or has expired; its operation tells which (looked up after).
      const pending = new Set(
        action.conversation.pending_approvals.map((each) => each.approval_id),
      );
      const approvals: Record<string, ApprovalState> = {};
      for (const [id, saved] of Object.entries(action.approvals)) {
        approvals[id] = pending.has(id)
          ? { ...saved, status: "pending" }
          : saved.status === "pending" || saved.status === "deciding"
            ? { ...saved, status: "decided" }
            : saved;
      }
      for (const approval of action.conversation.pending_approvals) {
        approvals[approval.approval_id] ??= {
          operationId: approval.operation_id,
          status: "pending",
        };
      }
      return {
        ...state,
        conversation: action.conversation,
        entries: action.entries,
        approvals,
        operations: action.operations,
      };
    }
    case "sent":
      return {
        ...state,
        activeId: action.assistantId,
        entries: [
          ...state.entries,
          { kind: "customer", id: action.customerId, text: action.text },
          { kind: "assistant", id: action.assistantId, status: "waiting", text: "", steps: [] },
        ],
      };
    case "deciding":
      return {
        ...state,
        activeId: action.assistantId,
        approvals: {
          ...state.approvals,
          [action.approval.approval_id]: {
            operationId: action.approval.operation_id,
            status: "deciding",
            choice: action.decision,
          },
        },
        entries: [
          ...state.entries,
          {
            kind: "assistant",
            id: action.assistantId,
            approvalId: action.approval.approval_id,
            decision: action.decision,
            status: "waiting",
            text: "",
            steps: [],
          },
        ],
      };
    case "resuming":
      return {
        ...state,
        activeId: action.assistantId,
        entries: [
          ...state.entries,
          {
            kind: "assistant",
            id: action.assistantId,
            turnId: action.turnId,
            status: "streaming",
            text: "",
            steps: [],
          },
        ],
      };
    case "recovering":
      return { ...state, activeId: action.assistantId };
    case "event":
      return applyEvent(state, action.assistantId, action.event);
    case "polled":
      return applyResult(state, action.assistantId, action.result);
    case "refused": {
      const approvals =
        action.approvalId && state.approvals[action.approvalId]
          ? {
              ...state.approvals,
              [action.approvalId]: {
                ...state.approvals[action.approvalId]!,
                status: action.approvalStatus ?? ("pending" as const),
              },
            }
          : state.approvals;
      return {
        ...state,
        activeId: undefined,
        approvals,
        entries: state.entries.map((entry) =>
          entry.id === action.assistantId
            ? { kind: "notice", id: action.noticeId, text: action.message }
            : entry,
        ),
      };
    }
    case "unknown": {
      const next = update(state, action.assistantId, (entry) => ({ ...entry, status: "unknown" }));
      return {
        ...next,
        activeId: next.activeId === action.assistantId ? undefined : next.activeId,
        entries: [...next.entries, { kind: "notice", id: action.noticeId, text: action.message }],
      };
    }
    case "operation": {
      const approvals = { ...state.approvals };
      const approvalId =
        action.approvalId ??
        Object.keys(approvals).find((id) => approvals[id]!.operationId === action.operationId);
      if (approvalId && approvals[approvalId]) {
        const current = approvals[approvalId];
        approvals[approvalId] = {
          ...current,
          status:
            action.status === "awaiting_approval"
              ? "pending"
              : action.status === "expired"
                ? "expired"
                : "decided",
          choice:
            current.choice ??
            (action.status === "rejected"
              ? "reject"
              : action.status === "expired" || action.status === "awaiting_approval"
                ? undefined
                : "approve"),
        };
      }
      return {
        ...state,
        approvals,
        operations: { ...state.operations, [action.operationId]: action.status },
      };
    }
    case "notice":
      return {
        ...state,
        entries: [...state.entries, { kind: "notice", id: action.noticeId, text: action.message }],
      };
  }
}

function applyEvent(state: State, assistantId: string, event: StreamEvent): State {
  if (event.type === "turn.final") return applyResult(state, assistantId, event.data.result);
  return update(state, assistantId, (entry) => {
    switch (event.type) {
      case "turn.started":
        return { ...entry, turnId: event.turn_id, status: "streaming" };
      case "turn.progress":
        return { ...entry, steps: addStep(entry.steps, event.data) };
      case "reply.delta":
        return { ...entry, text: entry.text + event.data.text };
      case "reply.reset":
        return { ...entry, text: "" };
      default:
        // A minor revision may add event types; clients ignore the ones they don't know.
        return entry;
    }
  });
}

function applyResult(state: State, assistantId: string, result: TurnResult): State {
  const finished = result.state === "finished";
  let next = update(state, assistantId, (entry) => ({
    ...entry,
    turnId: result.turn_id,
    status: finished ? "finished" : "streaming",
    text: result.reply?.text ?? (finished ? "" : entry.text),
    steps: finished ? finish(entry.steps, result) : entry.steps,
    result: finished ? result : entry.result,
  }));
  if (!finished) return next;

  const operations = { ...next.operations };
  for (const operation of result.operations ?? [])
    operations[operation.operation_id] = operation.status;
  const approvals = { ...next.approvals };
  if (result.pending_approval) {
    approvals[result.pending_approval.approval_id] = {
      operationId: result.pending_approval.operation_id,
      status: "pending",
    };
  }
  // Only the approval this turn resumed is decided by it.
  const entry = next.entries.find(
    (each): each is AssistantEntry => each.id === assistantId && each.kind === "assistant",
  );
  if (entry?.approvalId && approvals[entry.approvalId]) {
    approvals[entry.approvalId] = {
      ...approvals[entry.approvalId]!,
      status: "decided",
      choice: entry.decision,
    };
  }
  next = { ...next, operations, approvals };
  return { ...next, activeId: next.activeId === assistantId ? undefined : next.activeId };
}

/** True when the turn's record shows no committed change, so "nothing changed" is a fact, not a guess. */
export function nothingChanged(result: TurnResult | undefined): boolean {
  return !(result?.operations ?? []).some((operation) => operation.status === "committed");
}

/** Adds a progress step, keeping the circuit readable: one node per stage, one per tool. */
function addStep(
  steps: Step[],
  data: { stage: ProgressStage; route?: Route; tool?: string },
): Step[] {
  const step: Step | null =
    data.stage === "triage"
      ? { kind: "triage" }
      : data.stage === "routed" && data.route
        ? { kind: "route", route: data.route }
        : data.stage === "specialist"
          ? { kind: "specialist" }
          : data.stage === "tool_call" && data.tool
            ? { kind: "tool", name: data.tool }
            : data.stage === "awaiting_approval" || data.stage === "executing_operation"
              ? { kind: "gate", stage: data.stage }
              : null;
  if (!step) return steps;
  const seen = steps.some((existing) => JSON.stringify(existing) === JSON.stringify(step));
  return seen ? steps : [...steps, step];
}

/** The finished turn's facts complete the circuit: the route it took (if no progress said so) and the reply. */
function finish(steps: Step[], result: TurnResult): Step[] {
  let next = steps;
  if (result.route && !next.some((step) => step.kind === "route")) {
    next = [...next, { kind: "route", route: result.route }];
  }
  if (result.outcome === "awaiting_approval" && !next.some((step) => step.kind === "gate")) {
    next = [...next, { kind: "gate", stage: "awaiting_approval" }];
  }
  return result.reply ? [...next, { kind: "reply" }] : next;
}

function update(
  state: State,
  id: string,
  change: (entry: AssistantEntry) => AssistantEntry,
): State {
  return {
    ...state,
    entries: state.entries.map((entry) =>
      entry.id === id && entry.kind === "assistant" ? change(entry) : entry,
    ),
  };
}
