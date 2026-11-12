"use client";

import {
  nothingChanged,
  type ApprovalState,
  type AssistantEntry,
  type Entry,
  type OperationStatus,
} from "@/lib/support/conversation";
import {
  operationLabel,
  operationStatusLabel,
  replyKindLabel,
  routeLabel,
  WRITE_TOOLS,
} from "@/lib/support/labels";
import type { Approval } from "@/lib/support/types";

import { ApprovalCard } from "./approval-card";
import { BoltMark } from "./icons";
import { RichText } from "./rich-text";

const OPERATION_TONE: Partial<Record<OperationStatus, string>> = {
  committed: "text-status-success",
  failed: "text-status-danger",
};

export function Transcript({
  entries,
  approvals,
  operations,
  recovered,
  busy,
  onDecide,
}: {
  entries: Entry[];
  approvals: Record<string, ApprovalState>;
  operations: Record<string, OperationStatus>;
  /** Pending approvals the server reported that no entry shows. */
  recovered: Approval[];
  busy: boolean;
  onDecide: (approval: Approval, decision: "approve" | "reject") => void;
}) {
  return (
    <ol className="flex flex-col gap-8" aria-label="Messages">
      {entries.map((entry) => (
        <li key={entry.id} className="entry-in">
          {entry.kind === "customer" ? (
            <CustomerMessage text={entry.text} />
          ) : entry.kind === "notice" ? (
            <p
              role="status"
              className="border-status-warning bg-bg-secondary text-text-secondary rounded-md border-l-2 px-4 py-3 text-sm"
            >
              {entry.text}
            </p>
          ) : (
            <AssistantMessage
              entry={entry}
              approvals={approvals}
              operations={operations}
              busy={busy}
              onDecide={onDecide}
            />
          )}
        </li>
      ))}
      {recovered.map((approval) => (
        <li key={approval.approval_id} className="entry-in">
          <article className="grid grid-cols-[28px_1fr] gap-x-3">
            <span className="text-accent-primary mt-0.5">
              <BoltMark size={24} />
            </span>
            <div className="min-w-0">
              <p className="text-text-tertiary text-xs font-medium">
                VoltCart support · still waiting from earlier
              </p>
              <ApprovalCard
                approval={approval}
                state={
                  approvals[approval.approval_id] ?? {
                    operationId: approval.operation_id,
                    status: "pending",
                  }
                }
                operation={operations[approval.operation_id]}
                busy={busy}
                onDecide={(decision) => onDecide(approval, decision)}
              />
            </div>
          </article>
        </li>
      ))}
    </ol>
  );
}

function CustomerMessage({ text }: { text: string }) {
  return (
    <div className="flex justify-end">
      <p className="bg-bg-tertiary text-body max-w-[85%] rounded-lg rounded-br-sm px-4 py-2.5 whitespace-pre-wrap">
        <span className="sr-only">You: </span>
        {text}
      </p>
    </div>
  );
}

function AssistantMessage({
  entry,
  approvals,
  operations,
  busy,
  onDecide,
}: {
  entry: AssistantEntry;
  approvals: Record<string, ApprovalState>;
  operations: Record<string, OperationStatus>;
  busy: boolean;
  onDecide: (approval: Approval, decision: "approve" | "reject") => void;
}) {
  const result = entry.result;
  const approval = result?.pending_approval as Approval | undefined;
  const live = entry.status === "waiting" || entry.status === "streaming";

  return (
    <article className="grid grid-cols-[28px_1fr] gap-x-3" aria-busy={live}>
      <span className="text-accent-primary mt-0.5">
        <BoltMark size={24} />
      </span>
      <div className="min-w-0">
        <p className="text-text-tertiary text-xs font-medium">
          VoltCart support
          {entry.decision && (
            <span> · after you {entry.decision === "approve" ? "approved" : "rejected"}</span>
          )}
        </p>

        {/* No live region here: token-by-token fragments are noise. The page announces each finished reply once. */}
        <div className="text-body text-text-primary mt-1">
          {entry.text ? (
            <>
              <RichText text={entry.text} />
              {live && <span aria-hidden="true" className="caret" />}
            </>
          ) : live ? (
            <Thinking entry={entry} />
          ) : entry.status === "unknown" ? (
            <p className="text-text-tertiary text-sm">No reply was confirmed.</p>
          ) : null}
        </div>

        {/* The server's own words are written for the customer; claim "nothing changed" only from the record. */}
        {result?.outcome === "failed" && result.error && (
          <p className="text-body text-status-danger mt-2">
            {result.error.detail}
            {nothingChanged(result) &&
              !/nothing (was|has been) changed/i.test(result.error.detail) &&
              " Nothing was changed."}
          </p>
        )}
        {result?.outcome === "cancelled" && !entry.text && (
          <p className="text-text-secondary mt-2 text-sm">
            Stopped.{nothingChanged(result) && " Nothing was changed."}
          </p>
        )}

        {result?.operations
          ?.filter((operation) => operation.status !== "awaiting_approval")
          .map((operation) => (
            <p
              key={operation.operation_id}
              className={`bg-bg-tertiary mt-3 inline-flex items-center gap-2 rounded-sm px-2 py-1 text-xs font-medium ${
                OPERATION_TONE[operation.status] ?? "text-text-secondary"
              }`}
            >
              {operationLabel(operation.kind)}: {operationStatusLabel(operation.status)}
            </p>
          ))}

        {approval && approvals[approval.approval_id] && (
          <ApprovalCard
            approval={approval}
            state={approvals[approval.approval_id]!}
            operation={operations[approval.operation_id]}
            busy={busy}
            onDecide={(decision) => onDecide(approval, decision)}
          />
        )}

        <RouteLine entry={entry} />
      </div>
    </article>
  );
}

/** What the turn is doing before any reply text arrives, from its progress events. */
function Thinking({ entry }: { entry: AssistantEntry }) {
  const last = entry.steps.at(-1);
  const specialist = routeOf(entry);
  const text =
    last?.kind === "tool"
      ? `Using ${last.name}`
      : last?.kind === "gate"
        ? last.stage === "executing_operation"
          ? "Making the change"
          : "Preparing your approval"
        : last?.kind === "route" || last?.kind === "specialist"
          ? specialist
            ? `The ${specialist} specialist is on it`
            : "Finding the right specialist"
          : last?.kind === "triage"
            ? "Reading your message"
            : entry.decision
              ? "Recording your decision"
              : "Sending";
  return (
    <p className="text-text-tertiary flex items-center gap-2 text-sm">
      <span aria-hidden="true" className="dots" />
      {text}…
    </p>
  );
}

function routeOf(entry: AssistantEntry): string | undefined {
  const route = entry.steps.find((step) => step.kind === "route");
  return route?.kind === "route" && route.route.status === "routed"
    ? routeLabel(route.route)
    : undefined;
}

/** The route the turn took, compact and in the circuit's order: triage → specialist → tools → result. */
function RouteLine({ entry }: { entry: AssistantEntry }) {
  const steps = entry.steps;
  const parts: string[] = [];
  if (steps.some((step) => step.kind === "triage")) parts.push("triage");
  for (const step of steps)
    if (step.kind === "route") parts.push(routeLabel(step.route).toLowerCase());
  for (const step of steps) if (step.kind === "tool") parts.push(step.name);
  for (const step of steps) {
    if (step.kind === "gate")
      parts.push(step.stage === "awaiting_approval" ? "approval gate" : "approved");
  }
  const result = entry.result;
  const proposedWrite = steps.some((step) => step.kind === "tool" && WRITE_TOOLS.has(step.name));
  if (
    result?.outcome === "completed" &&
    proposedWrite &&
    !steps.some((step) => step.kind === "gate")
  ) {
    parts.push("gate refused");
  }
  if (result?.outcome === "failed") parts.push("failed");
  else if (result?.outcome === "cancelled") parts.push("stopped");
  else if (result?.reply) parts.push(replyKindLabel(result.reply.kind).toLowerCase());
  else if (entry.status === "unknown") parts.push("not confirmed");
  if (parts.length === 0) return null;
  return (
    <p className="text-text-tertiary mt-3 flex flex-wrap items-center gap-x-1.5 font-mono text-xs">
      <span className="sr-only">Route: </span>
      {parts.map((part, index) => (
        <span key={`${part}-${index}`} className="inline-flex items-center gap-1.5">
          {index > 0 && (
            <span aria-hidden="true" className="text-accent-primary">
              →
            </span>
          )}
          {part}
        </span>
      ))}
    </p>
  );
}
