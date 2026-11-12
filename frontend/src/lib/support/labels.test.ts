// @vitest-environment node
import type { AssistantEntry, Step } from "./conversation";
import { circuit } from "./labels";
import type { TurnResult } from "./types";

const routed: Step = { kind: "route", route: { status: "routed", target: "billing" } };

function entry(
  steps: Step[],
  result?: Partial<TurnResult>,
  extra: Partial<AssistantEntry> = {},
): AssistantEntry {
  return {
    kind: "assistant",
    id: "a1",
    status: result ? "finished" : "streaming",
    text: "",
    steps,
    result: result
      ? ({
          state: "finished",
          turn_id: "t",
          conversation_id: "c",
          client_turn_id: null,
          trace_id: "x",
          created_at: "",
          contract_version: "1.1",
          ...result,
        } as TurnResult)
      : undefined,
    ...extra,
  };
}

const node = (nodes: ReturnType<typeof circuit>, key: string) =>
  nodes.find((each) => each.key === key)!;

describe("circuit", () => {
  it("is all idle before any turn", () => {
    expect(circuit(undefined).map((each) => each.state)).toEqual([
      "idle",
      "idle",
      "idle",
      "idle",
      "idle",
    ]);
  });

  it("marks the node a running turn is working on as active", () => {
    const nodes = circuit(entry([{ kind: "triage" }, routed]));
    expect(nodes.map((each) => each.state)).toEqual(["lit", "lit", "active", "idle", "idle"]);
  });

  it("shows a proposed write as held at the gate while it waits for the customer", () => {
    const nodes = circuit(
      entry(
        [
          { kind: "triage" },
          routed,
          { kind: "tool", name: "refund_order" },
          { kind: "gate", stage: "awaiting_approval" },
        ],
        {
          outcome: "awaiting_approval",
          reply: { kind: "approval_request", text: "Approve?" },
        },
      ),
    );
    expect(node(nodes, "tools").items).toEqual([{ name: "refund_order", held: true }]);
    expect(node(nodes, "gate")).toMatchObject({
      state: "lit",
      detail: "Waiting for your decision",
    });
  });

  it("says the gate refused a write it declined in code, instead of 'nothing to change'", () => {
    const nodes = circuit(
      entry([{ kind: "triage" }, routed, { kind: "tool", name: "refund_order" }], {
        outcome: "completed",
        reply: { kind: "not_found", text: "Not found." },
      }),
    );
    expect(node(nodes, "gate")).toMatchObject({
      state: "lit",
      detail: "Refused in code: not on your account",
    });
    expect(node(nodes, "tools").detail).toBe("A change was proposed; the gate refused it");
  });

  const approvedTurn = (
    status: "committed" | "failed",
    outcome: "completed" | "cancelled" = "completed",
  ) =>
    entry(
      [
        routed,
        { kind: "gate", stage: "executing_operation" },
        { kind: "tool", name: "refund_order" },
      ],
      {
        outcome,
        reply: outcome === "completed" ? { kind: "approval_result", text: "Done." } : undefined,
        operations: [{ operation_id: "op", kind: "refund_order", status }],
      },
      { decision: "approve", approvalId: "appr" },
    );

  it("says the change ran only when its operation committed", () => {
    const nodes = circuit(approvedTurn("committed"));
    expect(node(nodes, "tools").items).toEqual([{ name: "refund_order", held: false }]);
    expect(node(nodes, "tools").detail).toBe("The change ran once, after your approval");
    expect(node(nodes, "gate").detail).toBe("Approved: ran once");
  });

  it("says an approved change wasn't carried out when its operation failed", () => {
    const nodes = circuit(approvedTurn("failed"));
    expect(node(nodes, "tools").items).toEqual([{ name: "refund_order", held: true }]);
    expect(node(nodes, "tools").detail).toBe("The change didn't run");
    expect(node(nodes, "gate").detail).toBe("Approved, but not carried out");
  });

  it("says a stopped approved change stopped before it ran, matching the reply node", () => {
    const nodes = circuit(approvedTurn("failed", "cancelled"));
    expect(node(nodes, "gate").detail).toBe("Stopped before it ran");
    expect(node(nodes, "reply").detail).toBe("Stopped: nothing changed");
  });

  it("doesn't call a stopped turn's proposed write 'refused'", () => {
    const nodes = circuit(
      entry([{ kind: "triage" }, routed, { kind: "tool", name: "refund_order" }], {
        outcome: "cancelled",
      }),
    );
    expect(node(nodes, "gate").detail).not.toMatch(/Refused/);
    expect(node(nodes, "tools").detail).not.toMatch(/refused/);
  });

  it("shows triage as not needed for a turn resumed from a decision", () => {
    const nodes = circuit(approvedTurn("committed"));
    expect(node(nodes, "triage")).toMatchObject({
      state: "skipped",
      detail: "Not needed: resumed from your decision",
    });
  });

  it("crosses out the gate when a read-only turn had nothing to change", () => {
    const nodes = circuit(
      entry([{ kind: "triage" }, routed, { kind: "tool", name: "get_invoice" }], {
        outcome: "completed",
        reply: { kind: "answer", text: "Here." },
      }),
    );
    expect(node(nodes, "gate")).toMatchObject({ state: "skipped", detail: "Nothing to change" });
  });

  it("colours a failure as an error, but not a stop the customer asked for", () => {
    const failed = circuit(
      entry([{ kind: "triage" }], {
        outcome: "failed",
        error: { code: "internal_error", detail: "x" },
      }),
    );
    const stopped = circuit(entry([{ kind: "triage" }], { outcome: "cancelled" }));
    expect(node(failed, "reply").tone).toBe("negative");
    expect(node(stopped, "reply").tone).toBeUndefined();
  });
});
