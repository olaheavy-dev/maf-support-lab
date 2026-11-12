"use client";

import { useEffect, useLayoutEffect, useRef } from "react";

import type { AssistantEntry } from "@/lib/support/conversation";
import { outcomeLabel, replyKindLabel } from "@/lib/support/labels";
import type { Approval } from "@/lib/support/types";
import { useSupport } from "@/lib/support/use-support";

import { Circuit } from "./circuit";
import { Composer } from "./composer";
import { BoltMark, PlusIcon } from "./icons";
import { ThemeSwitch } from "./theme-switch";
import { Transcript } from "./transcript";
import { Welcome } from "./welcome";

const BACKEND = { dotnet: ".NET backend", python: "Python backend" } as const;

export function SupportApp() {
  const { state, busy, send, decide, stop, startOver } = useSupport();
  const running = state.activeId !== undefined;
  const latest = [...state.entries]
    .reverse()
    .find((entry): entry is AssistantEntry => entry.kind === "assistant");
  const active = state.entries.find(
    (entry): entry is AssistantEntry => entry.id === state.activeId && entry.kind === "assistant",
  );
  // Approvals the server says are pending but no entry here shows (their turn's reply never reached this
  // page, for example after a dropped stream and a reload): shown on their own so they can be decided.
  const shown = new Set(
    state.entries.flatMap((entry) =>
      entry.kind === "assistant" && entry.result?.pending_approval
        ? [entry.result.pending_approval.approval_id]
        : [],
    ),
  );
  const recovered = (state.conversation?.pending_approvals ?? []).filter(
    (approval) => !shown.has(approval.approval_id),
  ) as Approval[];
  const pane = useRef<HTMLDivElement>(null);
  const following = useRef(true);

  // Follow the conversation as it grows, but only if the reader is already at the bottom: someone who
  // scrolled up to re-read isn't pulled away. No smooth scrolling for people who asked for less motion.
  useLayoutEffect(() => {
    const element = pane.current;
    if (!element || state.entries.length === 0 || !following.current) return;
    const reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    element.scrollTo({ top: element.scrollHeight, behavior: reduce ? "auto" : "smooth" });
  }, [state.entries]);

  useEffect(() => {
    const element = pane.current;
    if (!element) return;
    const onScroll = () => {
      following.current = element.scrollHeight - element.scrollTop - element.clientHeight < 80;
    };
    element.addEventListener("scroll", onScroll, { passive: true });
    return () => element.removeEventListener("scroll", onScroll);
  }, []);

  // One announcement per finished reply, for screen readers: its kind, then its words.
  const announcement =
    latest?.status === "finished" && latest.result
      ? `${latest.result.reply ? replyKindLabel(latest.result.reply.kind) : outcomeLabel(latest.result.outcome)}. ${latest.text}`
      : "";

  return (
    // An app-style frame: the page never scrolls; the conversation scrolls in its own pane between a fixed
    // header and the composer, and the circuit stays beside it.
    <div className="flex h-dvh flex-col overflow-hidden">
      <header className="border-border-primary shrink-0 border-b">
        <div className="mx-auto flex h-16 max-w-[1200px] items-center gap-4 px-4 sm:px-6">
          <div className="text-accent-primary flex items-center gap-2.5">
            <BoltMark />
            <p className="font-display text-text-primary text-lg font-semibold tracking-tight">
              VoltCart <span className="text-text-tertiary font-sans font-normal">Support</span>
            </p>
          </div>
          <div className="ml-auto flex items-center gap-2 sm:gap-3">
            <p className="text-text-secondary hidden text-sm md:block">
              Ada Example <span className="text-text-tertiary font-mono text-xs">CUST-1001</span>
            </p>
            {state.conversation && (
              <p className="border-border-primary text-text-secondary hidden rounded-sm border px-2 py-0.5 font-mono text-xs sm:block">
                {BACKEND[state.conversation.backend] ?? state.conversation.backend}
              </p>
            )}
            <ThemeSwitch />
            <button
              type="button"
              onClick={startOver}
              disabled={busy || state.entries.length === 0}
              className="border-border-secondary hover:bg-bg-tertiary inline-flex h-9 items-center gap-1.5 rounded-md border px-3 text-sm font-medium transition-colors duration-150 disabled:cursor-not-allowed disabled:opacity-50"
            >
              <PlusIcon />
              <span className="hidden sm:inline">New conversation</span>
              <span className="sr-only sm:hidden">New conversation</span>
            </button>
          </div>
        </div>
      </header>

      <p className="sr-only" aria-live="polite">
        {announcement}
      </p>

      <div className="mx-auto grid min-h-0 w-full max-w-[1200px] flex-1 lg:grid-cols-[minmax(0,1fr)_300px] lg:gap-12 lg:px-6">
        <main className="flex min-h-0 min-w-0 flex-col">
          <div
            ref={pane}
            role="region"
            aria-label="Conversation"
            tabIndex={0}
            className="min-h-0 flex-1 overflow-y-auto px-4 sm:px-6 lg:px-0"
          >
            <div className="mx-auto w-full max-w-[720px] py-6 sm:py-8">
              {state.entries.length === 0 && recovered.length === 0 ? (
                <Welcome onPick={send} disabled={busy} />
              ) : (
                <Transcript
                  entries={state.entries}
                  approvals={state.approvals}
                  operations={state.operations}
                  recovered={recovered}
                  busy={busy}
                  onDecide={decide}
                />
              )}
            </div>
          </div>
          <div className="shrink-0 px-4 pt-3 pb-4 sm:px-6 sm:pb-6 lg:px-0">
            <div className="mx-auto w-full max-w-[720px]">
              <Composer
                busy={busy}
                running={running}
                activeTurnId={active?.turnId}
                onSend={send}
                onStop={stop}
              />
              <p className="text-text-tertiary mt-2 text-center text-xs">
                A demonstration with synthetic data. Changes are simulated and always wait for your
                approval.
              </p>
            </div>
          </div>
        </main>

        <aside
          aria-label="How the latest turn ran"
          className="hidden min-h-0 overflow-y-auto lg:block"
        >
          <div className="py-8">
            <h2 className="text-sm font-semibold">How it ran</h2>
            <p className="text-text-tertiary mt-1 text-xs leading-relaxed">
              {latest
                ? "The latest turn, as the server reported it."
                : "Each message runs this circuit. Code routes it and checks every change; the model only answers."}
            </p>
            <div className="mt-6">
              <Circuit entry={latest} />
            </div>
            {latest?.result && (
              <dl className="border-border-primary mt-8 grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1.5 border-t pt-4 text-xs">
                <dt className="text-text-tertiary">Outcome</dt>
                <dd className="text-text-secondary">{outcomeLabel(latest.result.outcome)}</dd>
                <dt className="text-text-tertiary">Turn</dt>
                <dd
                  className="text-text-secondary truncate font-mono"
                  title={latest.result.turn_id}
                >
                  {latest.result.turn_id}
                </dd>
                <dt className="text-text-tertiary">Trace</dt>
                <dd
                  className="text-text-secondary truncate font-mono"
                  title={latest.result.trace_id}
                >
                  {latest.result.trace_id}
                </dd>
              </dl>
            )}
          </div>
        </aside>
      </div>
    </div>
  );
}
