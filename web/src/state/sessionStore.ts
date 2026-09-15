export type ConnectionStatus = "idle" | "connecting" | "ready" | "reconnecting" | "failed";

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
  entries: HistoryEntry[];
  liveResponseId: string | null;
  tombstones: Record<string, "interrupted" | "completed" | "failed">;
  lastServerSequence: number;
  streamId: string | null;
  muted: boolean;
  draft: string;
  error: string | null;
  preflightReady: boolean;
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
  entries: [],
  liveResponseId: null,
  tombstones: {},
  lastServerSequence: 0,
  streamId: null,
  muted: false,
  draft: "",
  error: null,
  preflightReady: false
});

function asString(value: unknown): string {
  return typeof value === "string" ? value : String(value ?? "");
}

function asNumber(value: unknown): number {
  return typeof value === "number" ? value : Number(value ?? 0);
}

function asHistory(raw: unknown): HistoryEntry[] {
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
      createdAt: asString(row.createdAt)
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

export function applyServerEvent(state: SessionView, event: ServerEvent): SessionView {
  if (state.attachmentId && event.attachmentId && event.attachmentId !== state.attachmentId && event.type !== "session.ready") {
    return state;
  }

  if (event.type !== "session.ready" && event.sequence <= state.lastServerSequence) {
    return state;
  }

  if (event.type !== "session.ready" && state.lastServerSequence > 0 && event.sequence > state.lastServerSequence + 1) {
    return { ...state, error: "Control sequence gap. Reconnect required.", connection: "failed" };
  }

  if (event.responseId && state.tombstones[event.responseId] && event.type.startsWith("agent.text")) {
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
        entries: asHistory(payload.history),
        liveResponseId: payload.activeResponseId == null ? null : asString(payload.activeResponseId),
        tombstones: {},
        lastServerSequence: event.sequence,
        streamId: payload.streamId == null ? null : asString(payload.streamId),
        muted: Boolean(payload.muted),
        error: null,
        preflightReady: false
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
            text: nextText,
            receivedTextEndExclusive: nextText.length,
            heardTextEndExclusive: state.mode === "text" ? nextText.length : entry.heardTextEndExclusive
          };
        })
      };
    }
    case "agent.text.completed":
      return { ...state, lastServerSequence: event.sequence };
    case "agent.response.interrupted":
    case "agent.response.completed": {
      const status = event.type === "agent.response.interrupted"
        ? "interrupted"
        : asString(event.payload.status) === "failed" ? "failed" : "completed";
      const tombstones = event.responseId
        ? { ...state.tombstones, [event.responseId]: status as "interrupted" | "completed" | "failed" }
        : state.tombstones;
      return {
        ...state,
        liveResponseId: state.liveResponseId === event.responseId ? null : state.liveResponseId,
        tombstones,
        lastServerSequence: event.sequence,
        entries: state.entries.map((entry) =>
          entry.responseId === event.responseId ? { ...entry, status } : entry)
      };
    }
    case "session.state.changed": {
      const mode = asString(event.payload.mode) === "voice" ? "voice" : "text";
      const pendingMode = event.payload.pendingMode == null
        ? null
        : asString(event.payload.pendingMode) === "voice" ? "voice" : "text";
      return {
        ...state,
        lastServerSequence: event.sequence,
        status: asString(event.payload.status) || state.status,
        mode,
        pendingMode,
        streamId: event.payload.streamId == null ? null : asString(event.payload.streamId),
        muted: Boolean(event.payload.muted),
        preflightReady: mode === "voice" ? false : pendingMode === "voice" ? state.preflightReady : false
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
    case "error":
      return {
        ...state,
        lastServerSequence: event.sequence,
        error: asString(event.payload.message)
      };
    default:
      return { ...state, lastServerSequence: event.sequence };
  }
}
