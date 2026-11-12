import { act, renderHook, waitFor } from "@testing-library/react";
import { readFileSync } from "node:fs";
import { join } from "node:path";

import * as client from "./client";
import { SupportApiError } from "./client";
import type { Conversation, StreamEvent, TurnResult } from "./types";
import { useSupport } from "./use-support";

vi.mock("./client", async (original) => {
  const actual = await original<typeof import("./client")>();
  return {
    ...actual,
    createConversation: vi.fn(),
    getConversation: vi.fn(),
    getTurn: vi.fn(),
    getOperation: vi.fn(),
    cancelTurn: vi.fn(),
    streamTurn: vi.fn(),
    streamDecision: vi.fn(),
  };
});

const mocked = vi.mocked(client);
// jsdom gives import.meta.url a non-file scheme, so resolve from the frontend folder (vitest's root).
const examples = join(process.cwd(), "..", "contracts", "examples", "valid") + "/";
const completed = JSON.parse(
  readFileSync(`${examples}stream-completed.json`, "utf8"),
) as StreamEvent[];
const finalResult = (completed.at(-1) as Extract<StreamEvent, { type: "turn.final" }>).data.result;

const conversation: Conversation = {
  contract_version: "1.1",
  conversation_id: finalResult.conversation_id,
  backend: "dotnet",
  created_at: "2026-11-12T00:00:00Z",
  active_turn_id: null,
  pending_approvals: [],
};

async function* yieldAll(events: StreamEvent[]): AsyncGenerator<StreamEvent> {
  for (const event of events) yield event;
}

async function* never(): AsyncGenerator<StreamEvent> {
  await new Promise(() => {});
}

beforeEach(() => {
  vi.resetAllMocks();
  sessionStorage.clear();
  mocked.createConversation.mockResolvedValue(conversation);
});

async function ready() {
  const hook = renderHook(() => useSupport());
  await waitFor(() => expect(hook.result.current.busy).toBe(false));
  return hook;
}

describe("useSupport", () => {
  it("streams a message into a finished reply", async () => {
    mocked.streamTurn.mockImplementation(() => yieldAll(completed));
    const { result } = await ready();
    act(() => void result.current.send("Where is my order ORD-1002?"));
    await waitFor(() => expect(result.current.busy).toBe(false));
    const reply = result.current.state.entries.at(-1);
    expect(reply).toMatchObject({
      kind: "assistant",
      status: "finished",
      text: finalResult.reply!.text,
    });
  });

  it("refuses a second message while one is in flight, so the composer keeps its text", async () => {
    mocked.streamTurn.mockImplementation(() => never());
    const { result } = await ready();
    let first = false;
    let second = true;
    act(() => {
      first = result.current.send("one");
      second = result.current.send("two");
    });
    expect(first).toBe(true);
    expect(second).toBe(false);
    await waitFor(() => expect(mocked.streamTurn).toHaveBeenCalledTimes(1));
  });

  it("after a stream that ended before any event, re-sends the very same request once", async () => {
    mocked.streamTurn
      .mockImplementationOnce(() => yieldAll([]))
      .mockImplementationOnce(() => yieldAll(completed));
    const { result } = await ready();
    act(() => void result.current.send("hello"));
    await waitFor(() => expect(result.current.busy).toBe(false));
    expect(mocked.streamTurn).toHaveBeenCalledTimes(2);
    const [first, second] = mocked.streamTurn.mock.calls;
    expect(second![1]).toEqual(first![1]); // same client_turn_id and message: the server returns the same turn
    expect(result.current.state.entries.at(-1)).toMatchObject({ status: "finished" });
  });

  it("says the outcome is unconfirmed, rather than guessing, when the retry also yields nothing", async () => {
    mocked.streamTurn.mockImplementation(() => yieldAll([]));
    const { result } = await ready();
    act(() => void result.current.send("hello"));
    await waitFor(() => expect(result.current.busy).toBe(false));
    const entries = result.current.state.entries;
    expect(entries.find((entry) => entry.kind === "assistant")).toMatchObject({
      status: "unknown",
    });
    expect(entries.at(-1)).toMatchObject({ kind: "notice" });
    expect(result.current.state.activeId).toBeUndefined();
  });

  it("polls the turn when the stream drops after it started", async () => {
    mocked.streamTurn.mockImplementation(() => yieldAll(completed.slice(0, 1)));
    mocked.getTurn.mockResolvedValue(finalResult as TurnResult);
    const { result } = await ready();
    act(() => void result.current.send("hello"));
    await waitFor(() => expect(result.current.busy).toBe(false));
    expect(mocked.getTurn).toHaveBeenCalledWith(finalResult.conversation_id, finalResult.turn_id);
    expect(result.current.state.entries.at(-1)).toMatchObject({ status: "finished" });
  });

  it("explains a refusal in the customer's words and frees the conversation", async () => {
    mocked.streamTurn.mockImplementation(() => {
      throw new SupportApiError("turn_in_progress", "busy", 409);
    });
    const { result } = await ready();
    act(() => void result.current.send("hello"));
    await waitFor(() => expect(result.current.busy).toBe(false));
    expect(result.current.state.entries.at(-1)).toMatchObject({
      kind: "notice",
      text: "A reply is still on its way. Wait for it, or stop it first.",
    });
  });

  it("after a reload, resumes the server's active turn", async () => {
    sessionStorage.setItem(
      "voltcart-conversation",
      JSON.stringify({
        conversationId: conversation.conversation_id,
        entries: [],
        approvals: {},
        operations: {},
      }),
    );
    mocked.getConversation.mockResolvedValue({
      ...conversation,
      active_turn_id: finalResult.turn_id,
    });
    mocked.getTurn.mockResolvedValue(finalResult as TurnResult);
    const { result } = await ready();
    expect(mocked.getTurn).toHaveBeenCalledWith(conversation.conversation_id, finalResult.turn_id);
    expect(result.current.state.entries.at(-1)).toMatchObject({
      kind: "assistant",
      status: "finished",
    });
  });

  it("keeps the saved conversation when the backend is briefly unreachable on reload", async () => {
    sessionStorage.setItem(
      "voltcart-conversation",
      JSON.stringify({ conversationId: "conv", entries: [], approvals: {}, operations: {} }),
    );
    mocked.getConversation.mockRejectedValue(new SupportApiError("unreachable", "down"));
    await ready();
    expect(sessionStorage.getItem("voltcart-conversation")).not.toBeNull();
  });
});
