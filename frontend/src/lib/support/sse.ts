// Reads a turn's Server-Sent Events from a fetch response body. The turn routes are POST, so the
// browser's EventSource doesn't apply (contracts/README.md, Streaming).

import type { StreamEvent } from "./types";

/**
 * Yields each event's `data` document, in order. Comment lines (`: keepalive`) and blank events are
 * skipped; `data` lines of one event are joined with newlines, as the SSE format says. A stream that
 * ends mid-event drops the partial event: the contract makes `GET …/turns/{id}` authoritative then.
 */
export async function* readEvents(body: ReadableStream<Uint8Array>): AsyncGenerator<StreamEvent> {
  const reader = body.getReader();
  const decoder = new TextDecoder(); // streaming: a character split across chunks is kept for the next
  let buffer = "";
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) return;
      buffer += decoder.decode(value, { stream: true });
      let boundary: RegExpExecArray | null;
      while ((boundary = BLANK_LINE.exec(buffer)) !== null) {
        const block = buffer.slice(0, boundary.index);
        buffer = buffer.slice(boundary.index + boundary[0].length);
        const event = parseBlock(block);
        if (event) yield event;
      }
    }
  } finally {
    reader.releaseLock();
  }
}

/** Where an event ends: a blank line, in any of the three line-ending styles. Not global: exec restarts at 0. */
const BLANK_LINE = /\r\n\r\n|\n\n|\r\r/;

function parseBlock(block: string): StreamEvent | null {
  const data: string[] = [];
  for (const line of block.split(/\r\n|\n|\r/)) {
    if (line.startsWith(":")) continue; // a comment, e.g. the keepalive
    const colon = line.indexOf(":");
    const field = colon === -1 ? line : line.slice(0, colon);
    if (field !== "data") continue; // `event` and `id` repeat what the JSON already says
    const value = colon === -1 ? "" : line.slice(colon + 1);
    data.push(value.startsWith(" ") ? value.slice(1) : value);
  }
  if (data.length === 0) return null;
  return JSON.parse(data.join("\n")) as StreamEvent;
}
