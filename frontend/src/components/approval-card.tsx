"use client";

import { useEffect, useState } from "react";

import type { ApprovalState, OperationStatus } from "@/lib/support/conversation";
import { operationLabel, operationStatusLabel } from "@/lib/support/labels";
import type { Approval } from "@/lib/support/types";

import { CheckIcon, CrossIcon } from "./icons";

const FIELD: Record<string, string> = {
  order_id: "Order",
  reason: "Reason",
  subject: "Subject",
  description: "Description",
  priority: "Priority",
  related_order_id: "Related order",
};

/**
 * A write waiting for the customer, as a work order: what will happen, with exactly these arguments,
 * until when. The server bound them when it proposed the write; deciding can't change them. Once
 * decided, the card shows the customer's choice and the operation's authoritative status.
 */
export function ApprovalCard({
  approval,
  state,
  operation,
  busy,
  onDecide,
}: {
  approval: Approval;
  state: ApprovalState;
  operation: OperationStatus | undefined;
  busy: boolean;
  onDecide: (decision: "approve" | "reject") => void;
}) {
  const remaining = useRemaining(approval.expires_at);
  // The server decides expiry; locally we only stop offering the buttons when time is up.
  const timeUp = remaining <= 0 && state.status === "pending";
  const open = state.status === "pending" && !timeUp;
  const deciding = state.status === "deciding";
  const titleId = `approval-${approval.approval_id}`;

  return (
    <section
      aria-labelledby={titleId}
      className={`bg-bg-secondary mt-4 overflow-hidden rounded-lg border ${
        open || deciding ? "border-accent-primary" : "border-border-primary"
      }`}
    >
      <header className="border-border-primary flex items-baseline justify-between gap-4 border-b border-dashed px-4 py-3">
        <div>
          <p className="text-text-tertiary text-xs font-medium tracking-wide uppercase">
            {eyebrow(state, timeUp)}
          </p>
          <h3 id={titleId} className="text-body mt-0.5 font-semibold">
            {operationLabel(approval.kind)}
          </h3>
        </div>
        <Status
          state={state}
          operation={operation}
          timeUp={timeUp}
          remaining={remaining}
          expiresAt={approval.expires_at}
        />
      </header>

      <dl className="grid grid-cols-[max-content_1fr] gap-x-6 gap-y-1.5 px-4 py-3 text-sm">
        {Object.entries(approval.arguments)
          .filter(([, value]) => value !== null && value !== "")
          .map(([name, value]) => (
            <div key={name} className="contents">
              <dt className="text-text-tertiary">{FIELD[name] ?? name}</dt>
              <dd className="text-code text-text-primary font-mono break-words">{value}</dd>
            </div>
          ))}
      </dl>

      <p className="text-text-secondary px-4 pb-3 text-sm">{approval.summary}</p>

      {(open || deciding) && (
        <div className="border-border-primary bg-bg-primary flex flex-wrap items-center gap-2 border-t px-4 py-3">
          <button
            type="button"
            disabled={busy}
            onClick={() => onDecide("approve")}
            className="bg-accent-primary text-accent-contrast hover:bg-accent-primary-hover inline-flex h-10 items-center gap-2 rounded-md px-4 text-sm font-medium transition-colors duration-150 disabled:cursor-not-allowed disabled:opacity-60"
          >
            <CheckIcon />
            {deciding && state.choice === "approve" ? "Approving…" : "Approve"}
          </button>
          <button
            type="button"
            disabled={busy}
            onClick={() => onDecide("reject")}
            className="border-border-secondary text-text-primary hover:bg-bg-tertiary inline-flex h-10 items-center gap-2 rounded-md border px-4 text-sm font-medium transition-colors duration-150 disabled:cursor-not-allowed disabled:opacity-60"
          >
            <CrossIcon />
            {deciding && state.choice === "reject" ? "Rejecting…" : "Reject"}
          </button>
          <p className="text-text-tertiary ml-auto text-xs">
            Runs once, with exactly these details.
          </p>
        </div>
      )}
    </section>
  );
}

function eyebrow(state: ApprovalState, timeUp: boolean): string {
  if (state.status === "expired") return "Not decided in time";
  if (state.status === "decided") {
    return state.choice === "reject"
      ? "You rejected"
      : state.choice === "approve"
        ? "You approved"
        : "Decided";
  }
  return timeUp ? "Time's up" : "Needs your approval";
}

function Status({
  state,
  operation,
  timeUp,
  remaining,
  expiresAt,
}: {
  state: ApprovalState;
  operation: OperationStatus | undefined;
  timeUp: boolean;
  remaining: number;
  expiresAt: string;
}) {
  if (state.status === "expired")
    return <Badge tone="neutral">{operationStatusLabel("expired")}</Badge>;
  if (state.status === "decided") {
    // The choice alone doesn't say what happened: the operation's status does.
    if (!operation || operation === "awaiting_approval")
      return <Badge tone="neutral">Recorded</Badge>;
    return (
      <Badge
        tone={
          operation === "committed" ? "positive" : operation === "failed" ? "negative" : "neutral"
        }
      >
        {operationStatusLabel(operation)}
      </Badge>
    );
  }
  if (timeUp) return <Badge tone="neutral">Expired</Badge>;
  const minutes = Math.floor(remaining / 60);
  const seconds = String(remaining % 60).padStart(2, "0");
  const until = new Date(expiresAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  return (
    <p className="text-text-tertiary shrink-0 font-mono text-xs">
      <span aria-hidden="true">
        expires in {minutes}:{seconds}
      </span>
      <span className="sr-only">Expires at {until}</span>
    </p>
  );
}

function Badge({
  tone,
  children,
}: {
  tone: "positive" | "negative" | "neutral";
  children: React.ReactNode;
}) {
  const colour =
    tone === "positive"
      ? "text-status-success"
      : tone === "negative"
        ? "text-status-danger"
        : "text-text-secondary";
  return (
    <p className={`bg-bg-tertiary shrink-0 rounded-sm px-2 py-0.5 text-xs font-medium ${colour}`}>
      {children}
    </p>
  );
}

/** Seconds until `expiresAt`, ticking once a second. */
function useRemaining(expiresAt: string): number {
  const deadline = Date.parse(expiresAt);
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  return Math.max(0, Math.floor((deadline - now) / 1000));
}
