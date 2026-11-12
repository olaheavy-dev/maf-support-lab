"use client";

import { useRef, useState } from "react";

import { SendIcon, StopIcon } from "./icons";

/** The message box. Enter sends, Shift+Enter adds a line; while a reply runs, Stop asks it to stop. */
export function Composer({
  busy,
  running,
  activeTurnId,
  onSend,
  onStop,
}: {
  /** A request is in flight (or the page is restoring): nothing can be sent. */
  busy: boolean;
  /** A reply is running: show Stop instead of Send. */
  running: boolean;
  /** The running turn, once the server has named it: only then can it be stopped. */
  activeTurnId: string | undefined;
  /** Returns false if the message can't be sent now; the text then stays in the box. */
  onSend: (text: string) => boolean;
  /** Resolves false if the stop wasn't accepted, so Stop can be tried again. */
  onStop: () => Promise<boolean>;
}) {
  const [text, setText] = useState("");
  // Which turn a stop was asked for, so "Stopping…" never carries over to the next one.
  const [stoppingTurn, setStoppingTurn] = useState<string>();
  const stopping = activeTurnId !== undefined && stoppingTurn === activeTurnId;
  const box = useRef<HTMLTextAreaElement>(null);

  function send() {
    const message = text.trim();
    if (!message) return;
    if (!onSend(message)) return;
    setText("");
    box.current?.focus();
  }

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        send();
      }}
      className="border-border-secondary bg-bg-secondary focus-within:border-accent-primary focus-within:ring-focus-ring flex items-end gap-2 rounded-lg border p-2 focus-within:ring-2"
    >
      <label htmlFor="message" className="sr-only">
        Message VoltCart support
      </label>
      <textarea
        id="message"
        ref={box}
        rows={1}
        value={text}
        maxLength={4000}
        placeholder="Ask about an order, an invoice or a product…"
        onChange={(event) => setText(event.target.value)}
        onKeyDown={(event) => {
          if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) {
            event.preventDefault();
            send();
          }
        }}
        className="text-body text-text-primary placeholder:text-text-tertiary field-sizing-content max-h-40 min-h-10 flex-1 resize-none bg-transparent px-2 py-2 focus:outline-none"
      />
      {running ? (
        <button
          type="button"
          disabled={!activeTurnId || stopping}
          onClick={() => {
            setStoppingTurn(activeTurnId);
            void onStop().then((accepted) => {
              if (!accepted) setStoppingTurn(undefined);
            });
          }}
          className="border-border-secondary text-text-primary hover:bg-bg-tertiary inline-flex h-10 shrink-0 items-center gap-2 rounded-md border px-3 text-sm font-medium transition-colors duration-150 disabled:cursor-not-allowed disabled:opacity-60"
        >
          <StopIcon />
          {stopping ? "Stopping…" : "Stop"}
        </button>
      ) : (
        <button
          type="submit"
          aria-label="Send"
          disabled={busy || !text.trim()}
          className="bg-accent-primary text-accent-contrast hover:bg-accent-primary-hover disabled:bg-bg-tertiary disabled:text-text-tertiary grid size-10 shrink-0 place-items-center rounded-md transition-colors duration-150 disabled:cursor-not-allowed"
        >
          <SendIcon />
        </button>
      )}
    </form>
  );
}
