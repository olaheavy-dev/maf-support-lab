import { render, screen } from "@testing-library/react";

import type { Approval } from "@/lib/support/types";

import { Transcript } from "./transcript";

const approval: Approval = {
  approval_id: "appr_1",
  operation_id: "op_1",
  kind: "refund_order",
  arguments: { order_id: "ORD-1001", reason: "The headphones crackle." },
  summary: "Refund order ORD-1001 in full.",
  expires_at: new Date(Date.now() + 10 * 60_000).toISOString(),
} as Approval;

describe("Transcript", () => {
  it("shows a pending approval the server reported even when no entry carries it, so it can be decided", () => {
    render(
      <Transcript
        entries={[]}
        approvals={{}}
        operations={{}}
        recovered={[approval]}
        busy={false}
        onDecide={() => {}}
      />,
    );
    expect(screen.getByRole("heading", { name: "Refund order" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Approve/ })).toBeEnabled();
    expect(screen.getByText("ORD-1001")).toBeInTheDocument();
  });

  it("shows a decided approval's real outcome, with no buttons", () => {
    render(
      <Transcript
        entries={[]}
        approvals={{ appr_1: { operationId: "op_1", status: "decided", choice: "approve" } }}
        operations={{ op_1: "failed" }}
        recovered={[approval]}
        busy={false}
        onDecide={() => {}}
      />,
    );
    expect(screen.getByText("You approved")).toBeInTheDocument();
    expect(screen.getByText("Not carried out: nothing changed")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Approve/ })).toBeNull();
  });
});
