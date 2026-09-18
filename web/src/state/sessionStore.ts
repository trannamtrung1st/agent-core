import { create } from "zustand";
import type { AgentDescriptor, CatalogItem } from "../services/api";
import type { PendingAttachment } from "../services/attachments";

export type ConnectionStatus = "idle" | "connecting" | "ready" | "reconnecting" | "failed";

export type HistoryAttachment = {
  attachmentId: string;
  displayName: string;
  contentType: string;
};

export type HistoryBlock = {
  blockId: string;
  kind: string;
  text: string;
  fallbackText: string;
  attachmentId: string | null;
  artifactId: string | null;
};

export type HistoryEntry = {
  entryId: string;
  sequence: number;
  sourceEventId: string | null;
  role: "user" | "assistant";
  text: string;
  responseId: string | null;
  status: string;
  deliveryMode: "text" | "voice";
  heardTextEndExclusive: number;
  receivedTextEndExclusive: number;
  createdAt: string;
  attachments?: HistoryAttachment[];
  blocks?: HistoryBlock[];
  finishReason?: string | null;
};

export type ServerEvent = {
  protocolVersion: number;
  sessionId: string;
  attachmentId: string | null;
  eventId: string;
  sequence: number;
  timestamp: string;
  correlationId: string;
  causationId: string | null;
  responseId: string | null;
  type: string;
  payload: Record<string, unknown>;
};

export type PendingSendItem = {
  localId: string;
  eventId: string;
  text: string;
  attachmentIds: string[];
  attachments: PendingAttachment[];
  dispatching?: boolean;
  error?: string | null;
};

export type SessionView = {
  connection: ConnectionStatus;
  sessionId: string | null;
  attachmentId: string | null;
  agentName: string;
  agentRole: string;
  voiceAvailable: boolean;
  mode: "text" | "voice";
  pendingMode: "text" | "voice" | null;
  status: string;
  inputState: string;
  outputState: string;
  entries: HistoryEntry[];
  liveResponseId: string | null;
  tombstones: Record<string, "interrupted" | "completed" | "failed">;
  lastServerSequence: number;
  streamId: string | null;
  muted: boolean;
  draft: string;
  pendingAttachments: PendingAttachment[];
  pendingSendQueue: PendingSendItem[];
  error: string | null;
  errorFatal: boolean;
  errorHoldSequence: number;
  preflightReady: boolean;
  captureLive: boolean;
  voicePlaybackResponseId: string | null;
  pauseReason: string | null;
};

export const emptySession = (): SessionView => ({
  connection: "idle",
  sessionId: null,
  attachmentId: null,
  agentName: "",
  agentRole: "",
  voiceAvailable: false,
  mode: "text",
  pendingMode: null,
  status: "created",
  inputState: "idle",
  outputState: "idle",
  entries: [],
  liveResponseId: null,
  tombstones: {},
  lastServerSequence: 0,
  streamId: null,
  muted: false,
  draft: "",
  pendingAttachments: [],
  pendingSendQueue: [],
  error: null,
  errorFatal: false,
  errorHoldSequence: 0,
  preflightReady: false,
  captureLive: false,
  voicePlaybackResponseId: null,
  pauseReason: null
});

function asString(value: unknown): string {
  return typeof value === "string" ? value : String(value ?? "");
}

function asNumber(value: unknown): number {
  return typeof value === "number" ? value : Number(value ?? 0);
}

function isInFlightOutput(value: string): boolean {
  return (
    value === "waitingForAgent" ||
    value === "agentGenerating" ||
    value === "agentSpeaking" ||
    value === "processingAttachments" ||
    value === "runningTools"
  );
}

function nextOutputState(eventType: string, liveResponseId: string | null, current: string): string {
  if (liveResponseId != null) {
    return current;
  }

  if (eventType === "agent.response.interrupted") {
    return "interrupted";
  }

  return isInFlightOutput(current) ? "idle" : current;
}

export function isReadonlySession(state: Pick<SessionView, "sessionId" | "status">): boolean {
  return state.sessionId != null && state.status === "ended";
}

export function historyFromPayload(raw: unknown): HistoryEntry[] {
  if (!Array.isArray(raw)) {
    return [];
  }

  return raw.map((item) => {
    const row = item as Record<string, unknown>;
    return {
      entryId: asString(row.entryId),
      sequence: asNumber(row.sequence),
      sourceEventId: row.sourceEventId == null ? null : asString(row.sourceEventId),
      role: asString(row.role) === "assistant" ? "assistant" : "user",
      text: asString(row.text),
      responseId: row.responseId == null ? null : asString(row.responseId),
      status: asString(row.status),
      deliveryMode: asString(row.deliveryMode) === "voice" ? "voice" : "text",
      heardTextEndExclusive: asNumber(row.heardTextEndExclusive),
      receivedTextEndExclusive: asNumber(row.receivedTextEndExclusive),
      createdAt: asString(row.createdAt),
      attachments: asAttachments(row.attachments),
      blocks: asBlocks(row.blocks),
      finishReason: row.finishReason == null ? null : asString(row.finishReason)
    };
  });
}

function asBlocks(raw: unknown): HistoryBlock[] | undefined {
  if (!Array.isArray(raw) || raw.length === 0) {
    return undefined;
  }

  return raw.map((item) => {
    const row = item as Record<string, unknown>;
    return {
      blockId: asString(row.blockId),
      kind: asString(row.kind),
      text: asString(row.text),
      fallbackText: asString(row.fallbackText),
      attachmentId: row.attachmentId == null ? null : asString(row.attachmentId),
      artifactId: row.artifactId == null ? null : asString(row.artifactId)
    };
  });
}

function asAttachments(raw: unknown): HistoryAttachment[] | undefined {
  if (!Array.isArray(raw) || raw.length === 0) {
    return undefined;
  }

  return raw.map((item) => {
    const row = item as Record<string, unknown>;
    return {
      attachmentId: asString(row.attachmentId),
      displayName: asString(row.displayName),
      contentType: asString(row.contentType)
    };
  });
}

function upsert(entries: HistoryEntry[], next: HistoryEntry): HistoryEntry[] {
  const index = entries.findIndex((entry) => entry.entryId === next.entryId);
  if (index === -1) {
    return [...entries, next].sort((left, right) => left.sequence - right.sequence);
  }

  const copy = entries.slice();
  copy[index] = next;
  return copy;
}

export function hasControlSequenceGap(state: SessionView, event: ServerEvent): boolean {
  return event.type !== "session.ready"
    && state.lastServerSequence > 0
    && event.sequence > state.lastServerSequence + 1;
}

export function applyServerEvent(state: SessionView, event: ServerEvent): SessionView {
  if (state.attachmentId && event.attachmentId && event.attachmentId !== state.attachmentId && event.type !== "session.ready") {
    return state;
  }

  if (event.type !== "session.ready" && event.sequence <= state.lastServerSequence) {
    return state;
  }

  if (hasControlSequenceGap(state, event)) {
    return {
      ...state,
      error: "Control sequence gap. Reconnect required.",
      errorFatal: false,
      connection: "failed"
    };
  }

    if (event.responseId && state.tombstones[event.responseId] && (event.type.startsWith("agent.text") || event.type === "agent.block.upsert" || event.type === "playback.gain")) {
    return { ...state, lastServerSequence: event.sequence };
  }

  switch (event.type) {
    case "session.ready": {
      const payload = event.payload;
      const agent = (payload.agent ?? {}) as Record<string, unknown>;
      return {
        ...state,
        connection: "ready",
        attachmentId: event.attachmentId,
        sessionId: event.sessionId,
        agentName: asString(agent.name),
        agentRole: asString(agent.role),
        voiceAvailable: Boolean(agent.voiceAvailable),
        mode: asString(payload.mode) === "voice" ? "voice" : "text",
        pendingMode: payload.pendingMode == null ? null : asString(payload.pendingMode) === "voice" ? "voice" : "text",
        status: asString(payload.status),
        inputState: asString(payload.inputState) || "idle",
        outputState: asString(payload.outputState) || "idle",
        entries: historyFromPayload(payload.history),
        liveResponseId: payload.activeResponseId == null ? null : asString(payload.activeResponseId),
        tombstones: {},
        lastServerSequence: event.sequence,
        streamId: payload.streamId == null ? null : asString(payload.streamId),
        muted: Boolean(payload.muted),
        error: null,
        errorFatal: false,
        errorHoldSequence: 0,
        preflightReady: asString(payload.mode) === "voice" ? false : state.preflightReady,
        voicePlaybackResponseId: null
      };
    }
    case "agent.response.started": {
      const entry: HistoryEntry = {
        entryId: asString(event.payload.entryId),
        sequence: asNumber(event.payload.entrySequence),
        sourceEventId: null,
        role: "assistant",
        text: "",
        responseId: event.responseId,
        status: "streaming",
        deliveryMode: state.mode,
        heardTextEndExclusive: 0,
        receivedTextEndExclusive: 0,
        createdAt: event.timestamp
      };
      return {
        ...state,
        liveResponseId: event.responseId,
        entries: upsert(state.entries, entry),
        lastServerSequence: event.sequence
      };
    }
    case "agent.text.delta": {
      if (!event.responseId || event.responseId !== state.liveResponseId) {
        return { ...state, lastServerSequence: event.sequence };
      }

      const text = asString(event.payload.text);
      const start = asNumber(event.payload.textStart);
      return {
        ...state,
        lastServerSequence: event.sequence,
        entries: state.entries.map((entry) => {
          if (entry.responseId !== event.responseId) {
            return entry;
          }

          if (start !== entry.text.length) {
            return entry;
          }

          const nextText = entry.text + text;
          return {
            ...entry,
            text: nextText
          };
        })
      };
    }
    case "agent.text.completed":
      return { ...state, lastServerSequence: event.sequence };
    case "agent.block.upsert": {
      if (!event.responseId || event.responseId !== state.liveResponseId) {
        return { ...state, lastServerSequence: event.sequence };
      }

      const block: HistoryBlock = {
        blockId: asString(event.payload.blockId),
        kind: asString(event.payload.kind),
        text: asString(event.payload.text),
        fallbackText: asString(event.payload.fallbackText),
        attachmentId: event.payload.attachmentId == null ? null : asString(event.payload.attachmentId),
        artifactId: event.payload.artifactId == null ? null : asString(event.payload.artifactId)
      };
      return {
        ...state,
        lastServerSequence: event.sequence,
        entries: state.entries.map((entry) => {
          if (entry.responseId !== event.responseId) {
            return entry;
          }

          const blocks = entry.blocks ?? [];
          if (blocks.some((item) => item.blockId === block.blockId)) {
            return entry;
          }

          return { ...entry, blocks: [...blocks, block] };
        })
      };
    }
    case "agent.response.interrupted":
    case "agent.response.completed": {
      const status = event.type === "agent.response.interrupted"
        ? "interrupted"
        : asString(event.payload.status) === "failed" ? "failed" : "completed";
      const finishReason = event.type === "agent.response.completed"
        ? asString(event.payload.finishReason) || null
        : null;
      const tombstones = event.responseId
        ? { ...state.tombstones, [event.responseId]: status as "interrupted" | "completed" | "failed" }
        : state.tombstones;
      const liveResponseId = state.liveResponseId === event.responseId ? null : state.liveResponseId;
      return {
        ...state,
        liveResponseId,
        outputState: nextOutputState(event.type, liveResponseId, state.outputState),
        tombstones,
        lastServerSequence: event.sequence,
        entries: state.entries.map((entry) =>
          entry.responseId === event.responseId
            ? { ...entry, status, finishReason: finishReason ?? entry.finishReason ?? null }
            : entry)
      };
    }
    case "session.state.changed": {
      const mode = asString(event.payload.mode) === "voice" ? "voice" : "text";
      const pendingMode = event.payload.pendingMode == null
        ? null
        : asString(event.payload.pendingMode) === "voice" ? "voice" : "text";
      const status = asString(event.payload.status) || state.status;
      const pauseReason = event.payload.pauseReason == null ? null : asString(event.payload.pauseReason);
      const paused = status === "paused";
      return {
        ...state,
        connection: paused ? "idle" : state.connection,
        lastServerSequence: event.sequence,
        status,
        pauseReason: paused ? pauseReason : null,
        inputState: asString(event.payload.inputState) || state.inputState,
        outputState: asString(event.payload.outputState) || state.outputState,
        mode,
        pendingMode,
        streamId: event.payload.streamId == null ? null : asString(event.payload.streamId),
        muted: Boolean(event.payload.muted),
        preflightReady: mode === "voice" ? false : pendingMode === "voice" || state.preflightReady,
        error: state.errorFatal
          ? state.error
          : event.sequence === state.errorHoldSequence + 1
            ? state.error
            : null
      };
    }
    case "transcript.final": {
      const utteranceId = asString(event.payload.utteranceId);
      const text = asString(event.payload.text);
      const entryId = event.payload.entryId == null ? utteranceId : asString(event.payload.entryId);
      if (state.entries.some((entry) => entry.entryId === entryId && entry.role === "user")) {
        return { ...state, lastServerSequence: event.sequence };
      }

      const entry: HistoryEntry = {
        entryId,
        sequence: asNumber(event.payload.entrySequence) || (state.entries.at(-1)?.sequence ?? 0) + 1,
        sourceEventId: event.eventId,
        role: "user",
        text,
        responseId: null,
        status: "completed",
        deliveryMode: "voice",
        heardTextEndExclusive: text.length,
        receivedTextEndExclusive: text.length,
        createdAt: event.timestamp
      };
      return { ...state, lastServerSequence: event.sequence, entries: upsert(state.entries, entry) };
    }
    case "error": {
      const fatal = event.payload.fatal === true;
      return {
        ...state,
        lastServerSequence: event.sequence,
        error: asString(event.payload.message),
        errorFatal: fatal,
        errorHoldSequence: fatal ? 0 : event.sequence
      };
    }
    default:
      return { ...state, lastServerSequence: event.sequence };
  }
}

export type SessionStore = SessionView & {
  agents: AgentDescriptor[];
  selectedAgentId: string;
  catalogItems: CatalogItem[];
  catalogNextCursor: string | null;
  catalogHasMore: boolean;
  catalogIncludeArchived: boolean;
  catalogCapabilityLost: boolean;
  catalogError: string | null;
  catalogMutation: CatalogMutation | null;
  routeNotice: string | null;
};

export type CatalogMutationKind = "rename" | "archive" | "unarchive" | "delete";

export type CatalogMutation = {
  sessionId: string;
  kind: CatalogMutationKind;
};

export const emptyCatalog = () => ({
  catalogItems: [] as CatalogItem[],
  catalogNextCursor: null as string | null,
  catalogHasMore: false,
  catalogIncludeArchived: false,
  catalogCapabilityLost: false,
  catalogError: null as string | null,
  catalogMutation: null as CatalogMutation | null,
  routeNotice: null as string | null
});

export const useSessionStore = create<SessionStore>(() => ({
  ...emptySession(),
  agents: [],
  selectedAgentId: "examiner",
  ...emptyCatalog()
}));
