// The contract's shapes for the chat page. They come from the generated types (contracts/openapi.json ->
// src/lib/api/schema.d.ts); the few the generator can't express precisely (conditional schemas: stream
// event data per type, approval arguments) are narrowed here from contracts/schemas/stream-event.schema.json.
import type { components } from "@/lib/api/schema";

type Schemas = components["schemas"];

export type Conversation = Schemas["conversation.schema"];
export type TurnResult = Schemas["turn-result.schema"];
export type Problem = Schemas["problem.schema"];
export type Route = Schemas["route"];
export type RouteTarget = Schemas["route_target"];
export type Reply = Schemas["reply"];
export type ReplyKind = Reply["kind"];
export type TurnError = Schemas["turn_error"];
export type OperationKind = Schemas["operation_kind"];
export type OperationSummary = Schemas["operation_summary"];

/** A write waiting for the customer: the exact arguments are fixed when it is proposed. */
export type Approval = Omit<Schemas["approval"], "arguments"> & {
  arguments: Record<string, string | null>;
};

export type ProgressStage =
  "triage" | "routed" | "specialist" | "tool_call" | "awaiting_approval" | "executing_operation";

type Envelope<Type extends string, Data> = {
  contract_version: string;
  type: Type;
  conversation_id: string;
  turn_id: string;
  sequence: number;
  data: Data;
};

/** One Server-Sent Event of a turn (contract 1.1), with its data typed by `type`. */
export type StreamEvent =
  | Envelope<"turn.started", { client_turn_id: string | null; resumed_from_turn_id?: string }>
  | Envelope<"turn.progress", { stage: ProgressStage; route?: Route; tool?: string }>
  | Envelope<"reply.delta", { text: string }>
  | Envelope<"reply.reset", Record<string, never>>
  | Envelope<"turn.final", { result: TurnResult }>;
