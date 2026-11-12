// The chat page's calls to the support API, through the frontend's same-origin /api proxy. JSON calls
// use the typed client; the two streamed routes (a turn, an approval decision) read Server-Sent Events.
import { api } from "@/lib/api/browser";

import { readEvents } from "./sse";
import type { OperationStatus } from "./conversation";
import type { Conversation, Problem, StreamEvent, TurnResult } from "./types";

/** A request the API refused (an RFC 9457 problem), or one that never reached it. */
export class SupportApiError extends Error {
  constructor(
    readonly code: Problem["code"] | "unreachable" | "unexpected",
    message: string,
    readonly status?: number,
  ) {
    super(message);
    this.name = "SupportApiError";
  }
}

export async function createConversation(): Promise<Conversation> {
  const { data, error, response } = await guarded(() =>
    api.POST("/v1/conversations", { body: {} }),
  );
  if (!data) throw problem(error, response);
  return data;
}

export async function getConversation(conversationId: string): Promise<Conversation> {
  const { data, error, response } = await guarded(() =>
    api.GET("/v1/conversations/{conversation_id}", {
      params: { path: { conversation_id: conversationId } },
    }),
  );
  if (!data) throw problem(error, response);
  return data;
}

export async function getTurn(conversationId: string, turnId: string): Promise<TurnResult> {
  const { data, error, response } = await guarded(() =>
    api.GET("/v1/conversations/{conversation_id}/turns/{turn_id}", {
      params: { path: { conversation_id: conversationId, turn_id: turnId } },
    }),
  );
  if (!data) throw problem(error, response);
  return data;
}

export async function getOperation(operationId: string): Promise<{ status: OperationStatus }> {
  const { data, error, response } = await guarded(() =>
    api.GET("/v1/operations/{operation_id}", {
      params: { path: { operation_id: operationId } },
    }),
  );
  if (!data) throw problem(error, response);
  return data;
}

export async function cancelTurn(conversationId: string, turnId: string): Promise<void> {
  const { error, response } = await guarded(() =>
    api.POST("/v1/conversations/{conversation_id}/turns/{turn_id}/cancel", {
      params: { path: { conversation_id: conversationId, turn_id: turnId } },
    }),
  );
  // A turn that finished first can't be cancelled: that's the race, not a failure.
  if (!response.ok && (error as Problem | undefined)?.code !== "turn_already_finished") {
    throw problem(error, response);
  }
}

/** Sends one message and streams the turn's events. */
export function streamTurn(
  conversationId: string,
  body: { client_turn_id: string; message: string },
  signal?: AbortSignal,
): AsyncGenerator<StreamEvent> {
  return stream(`/api/v1/conversations/${encodeURIComponent(conversationId)}/turns`, body, signal);
}

/** Approves or rejects a pending write and streams the turn that resumes it. */
export function streamDecision(
  conversationId: string,
  approvalId: string,
  body: { operation_id: string; decision: "approve" | "reject" },
  signal?: AbortSignal,
): AsyncGenerator<StreamEvent> {
  const path = `/api/v1/conversations/${encodeURIComponent(conversationId)}/approvals/${encodeURIComponent(approvalId)}`;
  return stream(path, body, signal);
}

async function* stream(
  path: string,
  body: unknown,
  signal?: AbortSignal,
): AsyncGenerator<StreamEvent> {
  const response = await guarded(() =>
    fetch(path, {
      method: "POST",
      headers: { accept: "text/event-stream", "content-type": "application/json" },
      body: JSON.stringify(body),
      signal,
    }),
  );
  const type = response.headers.get("content-type") ?? "";
  if (!response.ok) throw problem(await response.json().catch(() => undefined), response);
  if (type.includes("text/event-stream") && response.body) {
    yield* readEvents(response.body);
    return;
  }
  // A turn that already exists (a repeated client_turn_id) may come back as plain JSON.
  const result = (await response.json()) as TurnResult;
  yield {
    contract_version: result.contract_version,
    type: "turn.final",
    conversation_id: result.conversation_id,
    turn_id: result.turn_id,
    sequence: 1,
    data: { result },
  };
}

async function guarded<T>(call: () => Promise<T>): Promise<T> {
  try {
    return await call();
  } catch (error) {
    if (error instanceof DOMException && error.name === "AbortError") throw error;
    throw new SupportApiError(
      "unreachable",
      "Couldn't reach the support API. Is the backend running?",
    );
  }
}

function problem(body: unknown, response: Response): SupportApiError {
  const details = body as Partial<Problem> | undefined;
  if (details?.code) {
    return new SupportApiError(
      details.code,
      details.detail ?? details.title ?? "The request was refused.",
      response.status,
    );
  }
  if (response.status === 503) {
    return new SupportApiError(
      "unreachable",
      "Couldn't reach the support API. Is the backend running?",
      503,
    );
  }
  return new SupportApiError(
    "unexpected",
    `The support API answered ${response.status}.`,
    response.status,
  );
}
