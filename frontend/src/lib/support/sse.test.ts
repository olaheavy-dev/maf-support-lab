// @vitest-environment node
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { readEvents } from "./sse";
import type { StreamEvent } from "./types";

const examples = fileURLToPath(new URL("../../../../contracts/examples/valid/", import.meta.url));
const example = (name: string) =>
  JSON.parse(readFileSync(`${examples}${name}`, "utf8")) as StreamEvent[];

/** The events as a server writes them: `id`, `event`, `data`, a blank line; then split into chunks. */
function wire(events: StreamEvent[], newline = "\n"): string {
  return events
    .map((event) =>
      [
        `id: ${event.sequence}`,
        `event: ${event.type}`,
        `data: ${JSON.stringify(event)}`,
        "",
        "",
      ].join(newline),
    )
    .join("");
}

function body(text: string, chunkSize: number): ReadableStream<Uint8Array> {
  const bytes = new TextEncoder().encode(text);
  return new ReadableStream({
    start(controller) {
      for (let i = 0; i < bytes.length; i += chunkSize)
        controller.enqueue(bytes.slice(i, i + chunkSize));
      controller.close();
    },
  });
}

async function collect(stream: ReadableStream<Uint8Array>): Promise<StreamEvent[]> {
  const events: StreamEvent[] = [];
  for await (const event of readEvents(stream)) events.push(event);
  return events;
}

describe("readEvents", () => {
  for (const name of [
    "stream-completed.json",
    "stream-awaiting-approval.json",
    "stream-reply-reset.json",
    "stream-failed.json",
  ]) {
    it(`reads the contract's ${name} however the bytes are split`, async () => {
      const events = example(name);
      for (const chunkSize of [1, 7, 64, 1_000_000]) {
        expect(await collect(body(wire(events), chunkSize))).toEqual(events);
      }
    });
  }

  it("accepts CRLF line endings", async () => {
    const events = example("stream-completed.json");
    expect(await collect(body(wire(events, "\r\n"), 5))).toEqual(events);
  });

  it("skips keepalive comments", async () => {
    const events = example("stream-completed.json");
    const text = `: keepalive\n\n${wire(events.slice(0, 1))}: keepalive\n\n${wire(events.slice(1))}`;
    expect(await collect(body(text, 3))).toEqual(events);
  });

  it("drops an event cut off mid-way, rather than guessing its end", async () => {
    const events = example("stream-completed.json");
    const text = wire(events);
    expect(await collect(body(text.slice(0, text.length - 10), 16))).toEqual(events.slice(0, -1));
  });

  it("decodes multi-byte characters split across chunks", async () => {
    const event = {
      ...example("stream-completed.json")[0]!,
      data: { text: "Ünïcödé 🎧 ok" },
    } as unknown as StreamEvent;
    expect(await collect(body(wire([event]), 1))).toEqual([event]);
  });
});
