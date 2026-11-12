// The words the page uses for the contract's codes, in one place so the transcript, the circuit and the
// approval card always say the same thing. Every lookup has a fallback: a minor contract revision may add
// values (contracts/README.md, Revision policy), and the page must keep working.
import {
  nothingChanged,
  type AssistantEntry,
  type OperationStatus,
  type Step,
} from "./conversation";
import type { OperationKind, ReplyKind, Route, RouteTarget, TurnResult } from "./types";

const TARGET: Record<RouteTarget, string> = {
  orders: "Orders",
  billing: "Billing",
  technical: "Technical",
};

const REPLY_KIND: Record<ReplyKind, string> = {
  answer: "Answered",
  clarification: "Asked to clarify",
  not_found: "Not found",
  not_eligible: "Not eligible",
  escalation_offer: "Offered a ticket",
  approval_request: "Needs your approval",
  approval_result: "Reported the result",
  refusal: "Declined",
};

const OPERATION: Record<OperationKind, string> = {
  cancel_order: "Cancel order",
  refund_order: "Refund order",
  create_ticket: "Open a support ticket",
};

const OPERATION_STATUS: Record<OperationStatus, string> = {
  awaiting_approval: "Waiting for you",
  rejected: "Rejected: nothing changed",
  expired: "Expired: nothing changed",
  running: "Running",
  committed: "Done",
  failed: "Not carried out: nothing changed",
};

const OUTCOME: Record<NonNullable<TurnResult["outcome"]>, string> = {
  completed: "Completed",
  awaiting_approval: "Waiting for your approval",
  failed: "Failed",
  cancelled: "Stopped",
};

export const replyKindLabel = (kind: ReplyKind) => REPLY_KIND[kind] ?? "Replied";
export const operationLabel = (kind: OperationKind) => OPERATION[kind] ?? "Change";
export const operationStatusLabel = (status: OperationStatus) => OPERATION_STATUS[status] ?? status;
export const outcomeLabel = (outcome: TurnResult["outcome"]) =>
  (outcome && OUTCOME[outcome]) ?? "Unknown";

/** The model's write tools: requesting one only proposes it; the approval gate holds it until you decide. */
export const WRITE_TOOLS = new Set(["cancel_order", "refund_order", "create_ticket"]);

export function routeLabel(route: Route): string {
  if (route.status === "routed" && route.target) return TARGET[route.target] ?? "Specialist";
  return route.unrouted_reason === "invalid_model_output"
    ? "Not routed: unclear answer"
    : "Not routed";
}

/** The circuit's fixed nodes, in order. Each turn lights the ones it reaches. */
export type Node = {
  key: "triage" | "specialist" | "tools" | "gate" | "reply";
  label: string;
  detail?: string;
  /** Tools, for the tools node: a write tool is "held" unless its change was committed. */
  items?: { name: string; held: boolean }[];
  state: "idle" | "lit" | "active" | "skipped";
  tone?: "negative";
};

export function circuit(entry: AssistantEntry | undefined): Node[] {
  const steps = entry?.steps ?? [];
  const result = entry?.result;
  const finished = entry?.status === "finished";
  const running =
    entry !== undefined && (entry.status === "waiting" || entry.status === "streaming");
  const resumed = entry?.decision !== undefined;
  const has = (kind: Step["kind"]) => steps.some((step) => step.kind === kind);
  const route = steps.find(
    (step): step is Extract<Step, { kind: "route" }> => step.kind === "route",
  )?.route;
  const tools = steps
    .filter((step): step is Extract<Step, { kind: "tool" }> => step.kind === "tool")
    .map((step) => step.name);
  const gate = steps.find((step): step is Extract<Step, { kind: "gate" }> => step.kind === "gate");
  const write = tools.some((name) => WRITE_TOOLS.has(name));
  const committed = !nothingChanged(result);
  // A write was proposed in a turn that completed without asking for approval: the gate refused it.
  const declined = finished && result?.outcome === "completed" && !gate && write;
  const executing = gate?.stage === "executing_operation";

  const nodes: Node[] = [
    resumed
      ? {
          key: "triage",
          label: "Triage",
          detail: "Not needed: resumed from your decision",
          state: "skipped",
        }
      : {
          key: "triage",
          label: "Triage",
          detail: route ? "Classified, then routed in code" : undefined,
          state: has("triage") || route ? "lit" : "idle",
        },
    {
      key: "specialist",
      label: route
        ? route.status === "routed"
          ? `${routeLabel(route)} specialist`
          : routeLabel(route)
        : "Specialist",
      detail:
        route?.status === "unrouted" ? "No specialist runs; you're asked to clarify" : undefined,
      state: route ? "lit" : "idle",
    },
    {
      key: "tools",
      label: "Tools",
      items: tools.map((name) => ({ name, held: WRITE_TOOLS.has(name) && !committed })),
      detail: !write
        ? tools.length === 0 && finished && route?.status === "routed"
          ? "None needed"
          : undefined
        : declined
          ? "A change was proposed; the gate refused it"
          : committed
            ? "The change ran once, after your approval"
            : executing && finished
              ? "The change didn't run"
              : "A change was proposed and held at the gate, not run",
      state: tools.length > 0 ? "lit" : finished ? "skipped" : "idle",
    },
    {
      key: "gate",
      label: "Approval gate",
      detail: gateDetail(),
      state: gate || declined ? "lit" : finished ? "skipped" : "idle",
    },
    {
      key: "reply",
      label: "Reply",
      detail: replyDetail(),
      state: finished ? "lit" : "idle",
      tone: result?.outcome === "failed" ? "negative" : undefined,
    },
  ];

  function gateDetail(): string | undefined {
    if (declined) {
      return result?.reply?.kind === "not_eligible"
        ? "Refused in code: not eligible"
        : result?.reply?.kind === "not_found"
          ? "Refused in code: not on your account"
          : "Refused in code";
    }
    if (gate?.stage === "awaiting_approval") return "Waiting for your decision";
    if (executing) {
      if (!finished) return "Approved: running once";
      if (committed) return "Approved: ran once";
      return result?.outcome === "cancelled"
        ? "Stopped before it ran"
        : "Approved, but not carried out";
    }
    if (entry?.decision === "reject") return "Rejected: nothing ran";
    return finished ? "Nothing to change" : undefined;
  }

  function replyDetail(): string | undefined {
    if (!result) return entry?.status === "unknown" ? "Not confirmed" : undefined;
    if (result.outcome === "failed")
      return nothingChanged(result) ? "Failed: nothing changed" : "Failed";
    if (result.outcome === "cancelled")
      return nothingChanged(result) ? "Stopped: nothing changed" : "Stopped";
    return result.reply ? replyKindLabel(result.reply.kind) : undefined;
  }

  // While the turn runs, the furthest node it has reached is the active one.
  if (running) {
    const reached = nodes
      .map((node) => node.state === "lit" || node.state === "skipped")
      .lastIndexOf(true);
    const next = Math.min(reached + 1, nodes.length - 1);
    if (nodes[next] && nodes[next].state === "idle")
      nodes[next] = { ...nodes[next], state: "active" };
  }
  return nodes;
}
