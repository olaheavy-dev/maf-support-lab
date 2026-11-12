// @vitest-environment node
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import {
  initialState,
  nothingChanged,
  reduce,
  type AssistantEntry,
  type State,
} from "./conversation";
import type { Approval, Conversation, StreamEvent, TurnResult } from "./types";

const examples = fileURLToPath(new URL("../../../../contracts/examples/valid/", import.meta.url));
const example = (name: string) =>
  JSON.parse(readFileSync(`${examples}${name}`, "utf8")) as StreamEvent[];
const finalOf = (name: string) =>
  (example(name).at(-1) as Extract<StreamEvent, { type: "turn.final" }>).data.result;

function run(name: string, start: State = initialState, id = "a1"): State {
  let state = reduce(start, {
    type: "sent",
    customerId: `c-${id}`,
    text: "hello",
    assistantId: id,
  });
  for (const event of example(name))
    state = reduce(state, { type: "event", assistantId: id, event });
  return state;
}

const assistant = (state: State, id = "a1") =>
  state.entries.find(
    (entry): entry is AssistantEntry => entry.kind === "assistant" && entry.id === id,
  )!;

function final(result: TurnResult): StreamEvent {
  return {
    contract_version: result.contract_version,
    type: "turn.final",
    conversation_id: result.conversation_id,
    turn_id: result.turn_id,
    sequence: 9,
    data: { result },
  };
}

/** A pending approval for a second, distinct write, as another turn would return it. */
function secondApproval(state: State): { state: State; approval: Approval } {
  const first = assistant(state).result!;
  const approval = {
    ...(first.pending_approval as unknown as Approval),
    approval_id: "appr_second",
    operation_id: "op_second",
  };
  const result: TurnResult = {
    ...first,
    turn_id: "turn_second",
    pending_approval: approval as unknown as TurnResult["pending_approval"],
  };
  let next = reduce(state, { type: "sent", customerId: "c2", text: "and this", assistantId: "a2" });
  next = reduce(next, { type: "event", assistantId: "a2", event: final(result) });
  return { state: next, approval };
}

function resumedResult(
  base: TurnResult,
  operationId: string,
  status: "committed" | "rejected" | "failed",
): TurnResult {
  return {
    ...base,
    turn_id: `turn_resumed_${operationId}`,
    client_turn_id: null,
    outcome: "completed",
    reply: { kind: "approval_result", text: "Done." },
    pending_approval: undefined,
    operations: [{ operation_id: operationId, kind: "refund_order", status }],
  };
}

describe("reduce", () => {
  it("shows the customer's message and a waiting reply as soon as it is sent", () => {
    const state = reduce(initialState, {
      type: "sent",
      customerId: "c1",
      text: "hello",
      assistantId: "a1",
    });
    expect(state.entries.map((entry) => entry.kind)).toEqual(["customer", "assistant"]);
    expect(assistant(state).status).toBe("waiting");
    expect(state.activeId).toBe("a1");
  });

  it("finishes with the authoritative reply from turn.final", () => {
    const state = run("stream-completed.json");
    const result = finalOf("stream-completed.json");
    expect(assistant(state)).toMatchObject({
      status: "finished",
      text: result.reply!.text,
      turnId: result.turn_id,
    });
    expect(state.activeId).toBeUndefined();
  });

  it("discards streamed text on reply.reset", () => {
    const events = example("stream-reply-reset.json");
    let state = reduce(initialState, {
      type: "sent",
      customerId: "c1",
      text: "hello",
      assistantId: "a1",
    });
    const reset = events.findIndex((event) => event.type === "reply.reset");
    for (const event of events.slice(0, reset + 1))
      state = reduce(state, { type: "event", assistantId: "a1", event });
    expect(assistant(state).text).toBe("");
  });

  it("ignores event types a newer minor revision may add", () => {
    let state = reduce(initialState, {
      type: "sent",
      customerId: "c1",
      text: "hello",
      assistantId: "a1",
    });
    const unknown = {
      ...example("stream-completed.json")[0]!,
      type: "turn.heartbeat",
      data: {},
    } as unknown as StreamEvent;
    state = reduce(state, { type: "event", assistantId: "a1", event: unknown });
    expect(assistant(state)).toMatchObject({ kind: "assistant", status: "waiting" });
  });

  it("draws the circuit from progress events and the result: route, gate, reply", () => {
    const state = run("stream-awaiting-approval.json");
    expect(assistant(state).steps.map((step) => step.kind)).toEqual(["route", "gate", "reply"]);
  });

  it("opens a pending approval, then settles only the one the resumed turn decided", () => {
    let state = run("stream-awaiting-approval.json");
    const first = assistant(state).result!.pending_approval as unknown as Approval;
    const second = secondApproval(state);
    state = second.state;
    expect(state.approvals[first.approval_id]!.status).toBe("pending");
    expect(state.approvals[second.approval.approval_id]!.status).toBe("pending");

    // Approve the first, and let its stream drop before any event: it stays "deciding".
    state = reduce(state, {
      type: "deciding",
      approval: first,
      decision: "approve",
      assistantId: "d1",
    });
    state = reduce(state, {
      type: "unknown",
      assistantId: "d1",
      noticeId: "n1",
      message: "dropped",
    });
    // Reject the second; its resumed turn must not settle the first.
    state = reduce(state, {
      type: "deciding",
      approval: second.approval,
      decision: "reject",
      assistantId: "d2",
    });
    state = reduce(state, {
      type: "event",
      assistantId: "d2",
      event: final(
        resumedResult(assistant(state).result!, second.approval.operation_id, "rejected"),
      ),
    });
    expect(state.approvals[second.approval.approval_id]).toMatchObject({
      status: "decided",
      choice: "reject",
    });
    expect(state.approvals[first.approval_id]!.status).toBe("deciding");

    // The first's real outcome arrives from its operation.
    state = reduce(state, {
      type: "operation",
      operationId: first.operation_id,
      status: "committed",
      approvalId: first.approval_id,
    });
    expect(state.approvals[first.approval_id]).toMatchObject({
      status: "decided",
      choice: "approve",
    });
    expect(state.operations[first.operation_id]).toBe("committed");
  });

  it("records the operation's real status from the resumed turn, not just the decision", () => {
    let state = run("stream-awaiting-approval.json");
    const approval = assistant(state).result!.pending_approval as unknown as Approval;
    state = reduce(state, { type: "deciding", approval, decision: "approve", assistantId: "d1" });
    state = reduce(state, {
      type: "event",
      assistantId: "d1",
      event: final(resumedResult(assistant(state).result!, approval.operation_id, "failed")),
    });
    expect(state.approvals[approval.approval_id]).toMatchObject({
      status: "decided",
      choice: "approve",
    });
    expect(state.operations[approval.operation_id]).toBe("failed");
  });

  it("closes an approval the server says has expired, instead of offering it again", () => {
    let state = run("stream-awaiting-approval.json");
    const approval = assistant(state).result!.pending_approval as unknown as Approval;
    state = reduce(state, { type: "deciding", approval, decision: "approve", assistantId: "d1" });
    state = reduce(state, {
      type: "refused",
      assistantId: "d1",
      noticeId: "n1",
      message: "Expired.",
      approvalId: approval.approval_id,
      approvalStatus: "expired",
    });
    expect(state.approvals[approval.approval_id]!.status).toBe("expired");
  });

  it("reopens an approval only when the refusal leaves it undecided (a transport failure)", () => {
    let state = run("stream-awaiting-approval.json");
    const approval = assistant(state).result!.pending_approval as unknown as Approval;
    state = reduce(state, { type: "deciding", approval, decision: "reject", assistantId: "d1" });
    state = reduce(state, {
      type: "refused",
      assistantId: "d1",
      noticeId: "n1",
      message: "x",
      approvalId: approval.approval_id,
      approvalStatus: "pending",
    });
    expect(state.approvals[approval.approval_id]!.status).toBe("pending");
    expect(state.activeId).toBeUndefined();
  });

  it("after a reload, opens only the server's pending approvals", () => {
    let state = run("stream-awaiting-approval.json");
    const approval = assistant(state).result!.pending_approval as unknown as Approval;
    const conversation = {
      contract_version: "1.1",
      conversation_id: "conv",
      backend: "dotnet",
      created_at: "2026-11-12T00:00:00Z",
      active_turn_id: null,
      pending_approvals: [],
    } as Conversation;
    state = reduce(initialState, {
      type: "restored",
      conversation,
      entries: state.entries,
      approvals: state.approvals,
      operations: state.operations,
    });
    expect(state.approvals[approval.approval_id]!.status).toBe("decided");
  });

  it("marks a turn whose outcome couldn't be confirmed, and frees the conversation", () => {
    let state = reduce(initialState, {
      type: "sent",
      customerId: "c1",
      text: "hello",
      assistantId: "a1",
    });
    state = reduce(state, {
      type: "unknown",
      assistantId: "a1",
      noticeId: "n1",
      message: "Couldn't confirm.",
    });
    expect(assistant(state).status).toBe("unknown");
    expect(state.activeId).toBeUndefined();
    expect(state.entries.at(-1)).toEqual({ kind: "notice", id: "n1", text: "Couldn't confirm." });
  });

  it("keeps waiting while a polled turn is still running", () => {
    let state = reduce(initialState, {
      type: "sent",
      customerId: "c1",
      text: "hello",
      assistantId: "a1",
    });
    const running = { ...finalOf("stream-completed.json"), state: "running" as const };
    state = reduce(state, { type: "polled", assistantId: "a1", result: running });
    expect(state.activeId).toBe("a1");
    expect(assistant(state).status).toBe("streaming");
  });

  it("turns a refused message into a notice and frees the conversation", () => {
    let state = reduce(initialState, {
      type: "sent",
      customerId: "c1",
      text: "hello",
      assistantId: "a1",
    });
    state = reduce(state, {
      type: "refused",
      assistantId: "a1",
      noticeId: "n1",
      message: "Wait for the reply.",
    });
    expect(state.entries.at(-1)).toEqual({ kind: "notice", id: "n1", text: "Wait for the reply." });
    expect(state.activeId).toBeUndefined();
  });
});

describe("nothingChanged", () => {
  const base = finalOf("stream-completed.json");
  it("is true only when no operation committed", () => {
    expect(nothingChanged(base)).toBe(true);
    expect(
      nothingChanged({
        ...base,
        operations: [{ operation_id: "o", kind: "cancel_order", status: "failed" }],
      }),
    ).toBe(true);
    expect(
      nothingChanged({
        ...base,
        operations: [{ operation_id: "o", kind: "cancel_order", status: "committed" }],
      }),
    ).toBe(false);
  });
});
