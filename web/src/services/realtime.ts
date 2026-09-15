import { HttpTransportType, HubConnection, HubConnectionBuilder } from "@microsoft/signalr";
import { MessagePackHubProtocol } from "@microsoft/signalr-protocol-msgpack";
import { capture } from "../audio/capture";
import { MAX_QUEUED_SAMPLES } from "../audio/playback";
import { useChatStore } from "../state/chatStore";
import { applyServerEvent, emptySession, type ServerEvent } from "../state/sessionStore";
import { createSession, endSession, listAgents } from "./api";

let connection: HubConnection | null = null;
let commandSequence = 0;
let audioFramesSent = 0;
let audioOutputsReceived = 0;
let playbackConsumed = 0;
let playbackResponseId: string | null = null;
let playbackStarted = false;
let playbackFinal = false;
let progressTimer: number | null = null;
let disposed = false;

function uuid(): string {
  return crypto.randomUUID();
}

export function audioFramesSentCount(): number {
  return audioFramesSent;
}

export function playbackConsumedCount(): number {
  return playbackConsumed;
}

export function audioOutputsReceivedCount(): number {
  return audioOutputsReceived;
}

export function setDraft(draft: string): void {
  useChatStore.setState({ draft });
}

export function selectAgent(agentId: string): void {
  useChatStore.setState({ selectedAgentId: agentId });
}

function command(type: string, payload: Record<string, unknown>, sequence: number, responseId: string | null = null) {
  const snapshot = useChatStore.getState();
  return {
    protocolVersion: 1,
    sessionId: snapshot.sessionId,
    eventId: uuid(),
    sequence,
    timestamp: new Date().toISOString(),
    responseId,
    attachmentId: snapshot.attachmentId,
    type,
    payload
  };
}

async function invoke(
  method: string,
  type: string,
  payload: Record<string, unknown>,
  sequence: number,
  responseId: string | null = null
) {
  if (!connection) {
    throw new Error("Not connected.");
  }

  return connection.invoke(method, command(type, payload, sequence, responseId)) as Promise<{
    accepted?: boolean;
    error?: { message?: string };
  }>;
}

function handleEvent(raw: ServerEvent): void {
  useChatStore.setState(applyServerEvent(useChatStore.getState(), raw));
  if (raw.type === "agent.response.interrupted" && raw.responseId) {
    stopPlayback(raw.responseId);
  }

  syncCapture();
}

function bytesOf(data: unknown): Uint8Array {
  if (data instanceof Uint8Array) {
    return data;
  }

  if (data instanceof ArrayBuffer) {
    return new Uint8Array(data);
  }

  if (ArrayBuffer.isView(data)) {
    return new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
  }

  if (data && typeof data === "object" && "data" in (data as { data?: unknown }) && Array.isArray((data as { data: unknown }).data)) {
    return Uint8Array.from((data as { data: number[] }).data);
  }

  if (Array.isArray(data)) {
    return Uint8Array.from(data as number[]);
  }

  return new Uint8Array();
}

function handleAudioOutput(dto: {
  responseId?: string;
  data?: unknown;
  isFinal?: boolean;
}): void {
  audioOutputsReceived += 1;
  const responseId = dto.responseId ?? (dto as { ResponseId?: string }).ResponseId;
  if (!responseId || !connection) {
    return;
  }

  const pcm = bytesOf(dto.data ?? (dto as { Data?: unknown }).Data);
  const isFinal = Boolean(dto.isFinal ?? (dto as { IsFinal?: boolean }).IsFinal);
  if (pcm.length / 2 > MAX_QUEUED_SAMPLES) {
    useChatStore.setState({ error: "Audio output exceeded the 2 second queue." });
    return;
  }

  if (pcm.length > 0) {
    capture.enqueuePlayback(responseId, pcm);
  }

  if (!playbackStarted) {
    playbackStarted = true;
    playbackResponseId = responseId;
    capture.setPlaybackListener((consumed) => {
      playbackConsumed = consumed;
    });
    void sendPlayback("PlaybackStarted", "playback.started", responseId, 0);
    startProgress();
  }

  if (isFinal) {
    playbackFinal = true;
  }
}

function startProgress(): void {
  stopProgress();
  progressTimer = window.setInterval(() => {
    const responseId = playbackResponseId;
    if (!responseId) {
      return;
    }

    const consumed = capture.playbackConsumed();
    playbackConsumed = consumed;
    void sendPlayback("PlaybackProgress", "playback.progress", responseId, consumed);
    if (playbackFinal) {
      void sendPlayback("PlaybackCompleted", "playback.completed", responseId, consumed);
      stopProgress();
    }
  }, 100);
}

function stopProgress(): void {
  if (progressTimer !== null) {
    window.clearInterval(progressTimer);
    progressTimer = null;
  }
}

async function sendPlayback(method: string, type: string, responseId: string, consumed: number): Promise<void> {
  commandSequence += 1;
  await invoke(method, type, { consumedSamples: consumed, textEndExclusive: 0 }, commandSequence, responseId);
}

function stopPlayback(responseId?: string): void {
  stopProgress();
  capture.flushPlayback(responseId ?? playbackResponseId ?? "");
  playbackStarted = false;
  playbackFinal = false;
  playbackResponseId = null;
  capture.setPlaybackListener(null);
}

function syncCapture(): void {
  const state = useChatStore.getState();
  if (state.connection !== "ready") {
    capture.release();
    return;
  }

  if (state.mode === "voice" && state.streamId && connection) {
    if (!capture.isStreaming()) {
      const hub = connection;
      const sessionId = state.sessionId;
      const attachmentId = state.attachmentId;
      const streamId = state.streamId;
      capture.start({
        sendAudio: async (frame) => {
          if (!hub || !sessionId || !attachmentId || !streamId) {
            return;
          }

          audioFramesSent += 1;
          await hub.send("SendAudio", {
            protocolVersion: 1,
            sessionId,
            attachmentId,
            streamId,
            frameSequence: frame.frameSequence,
            sampleOffset: frame.sampleOffset,
            data: frame.data
          });
        },
        speechStarted: async (utteranceId, sampleOffset, activityScore) => {
          commandSequence += 1;
          await invoke("SpeechStarted", "user.speech.started", {
            streamId,
            utteranceId,
            sampleOffset,
            activityScore
          }, commandSequence);
        },
        speechEnded: async (utteranceId, sampleOffset, activityScore) => {
          commandSequence += 1;
          await invoke("SpeechEnded", "user.speech.ended", {
            streamId,
            utteranceId,
            sampleOffset,
            durationMs: 0,
            activityScore
          }, commandSequence);
        }
      });
    }

    return;
  }

  if (state.pendingMode === "voice" || state.preflightReady) {
    return;
  }

  capture.release();
}

async function startConnection(sessionId: string): Promise<void> {
  await stopConnection();
  commandSequence = 0;
  audioFramesSent = 0;
  audioOutputsReceived = 0;
  connection = new HubConnectionBuilder()
    .withUrl("/hubs/session", {
      skipNegotiation: true,
      transport: HttpTransportType.WebSockets
    })
    .withHubProtocol(new MessagePackHubProtocol())
    .withAutomaticReconnect([0, 2000, 5000, 10000])
    .build();
  connection.on("SessionEvent", handleEvent);
  connection.on("AudioOutput", handleAudioOutput);
  connection.onreconnecting(() => {
    stopPlayback();
    capture.release();
    useChatStore.setState({ connection: "reconnecting", pendingMode: null, preflightReady: false });
  });
  connection.onreconnected(async () => {
    commandSequence = 0;
    await invoke("Attach", "session.attach", { lastServerSequence: useChatStore.getState().lastServerSequence || null }, 0);
  });
  connection.onclose(() => {
    stopPlayback();
    capture.release();
    if (!disposed) {
      useChatStore.setState({ connection: "reconnecting", pendingMode: null, preflightReady: false });
    }
  });
  useChatStore.setState({ connection: "connecting", sessionId, pendingMode: null, preflightReady: false });
  await connection.start();
  const ack = await invoke("Attach", "session.attach", { lastServerSequence: null }, 0);
  if (!ack?.accepted) {
    useChatStore.setState({ error: ack?.error?.message ?? "Attach failed.", connection: "failed" });
  }
}

async function stopConnection(): Promise<void> {
  stopPlayback();
  capture.release();
  if (!connection) {
    return;
  }

  const current = connection;
  connection = null;
  current.off("SessionEvent");
  current.off("AudioOutput");
  try {
    await current.stop();
  } catch {
    // ignored
  }
}

export async function bootstrap(): Promise<void> {
  const agents = await listAgents();
  useChatStore.setState({ agents, selectedAgentId: agents[0]?.id ?? "examiner" });
}

export async function startConversation(): Promise<void> {
  const created = await createSession(useChatStore.getState().selectedAgentId, "text");
  await startConnection(created.sessionId);
}

export async function sendDraft(): Promise<void> {
  const snapshot = useChatStore.getState();
  const text = snapshot.draft.trim();
  if (!text || snapshot.connection !== "ready") {
    return;
  }

  useChatStore.setState({
    draft: "",
    entries: [
      ...snapshot.entries,
      {
        entryId: uuid(),
        sequence: (snapshot.entries.at(-1)?.sequence ?? 0) + 0.5,
        sourceEventId: null,
        role: "user",
        text,
        responseId: null,
        status: "completed",
        deliveryMode: snapshot.mode,
        heardTextEndExclusive: text.length,
        receivedTextEndExclusive: text.length,
        createdAt: new Date().toISOString()
      }
    ]
  });
  commandSequence += 1;
  await invoke("SendText", "user.text", { text }, commandSequence);
}

export async function requestVoice(): Promise<void> {
  const snapshot = useChatStore.getState();
  if (!snapshot.voiceAvailable || snapshot.connection !== "ready") {
    return;
  }

  useChatStore.setState({ preflightReady: true, error: null });
  try {
    await capture.preflight();
  } catch (error) {
    capture.release();
    useChatStore.setState({
      preflightReady: false,
      error: error instanceof Error ? error.message : "Microphone preflight failed."
    });
    return;
  }
  commandSequence += 1;
  try {
    const ack = await invoke("SetMode", "session.mode.set", { mode: "voice" }, commandSequence);
    if (!ack?.accepted) {
      capture.release();
      useChatStore.setState({ preflightReady: false, error: ack?.error?.message ?? "Voice mode was rejected." });
    }
  } catch (error) {
    capture.release();
    useChatStore.setState({
      preflightReady: false,
      error: error instanceof Error ? error.message : "Voice mode failed."
    });
  }
}

export async function cancelVoice(): Promise<void> {
  capture.release();
  useChatStore.setState({ preflightReady: false });
  commandSequence += 1;
  await invoke("SetMode", "session.mode.set", { mode: "text" }, commandSequence);
}

export async function hangUp(): Promise<void> {
  disposed = true;
  const snapshot = useChatStore.getState();
  if (snapshot.sessionId) {
    commandSequence += 1;
    try {
      await invoke("EndSession", "session.end", { reason: "userEnded" }, commandSequence);
    } catch {
      await endSession(snapshot.sessionId);
    }
  }

  await stopConnection();
  capture.release();
  useChatStore.setState({
    ...emptySession(),
    agents: snapshot.agents,
    selectedAgentId: snapshot.selectedAgentId
  });
  disposed = false;
}

if (typeof window !== "undefined") {
  window.__agentCore = {
    audioFramesSent: audioFramesSentCount,
    disconnect: async () => {
      stopPlayback();
      capture.release();
      await stopConnection();
      useChatStore.setState({ connection: "reconnecting", pendingMode: null, preflightReady: false });
    },
    capturePrepared: () => capture.isPrepared() || capture.isStreaming(),
    workletLoaded: () => capture.workletLoaded(),
    outputWorkletLoaded: () => capture.outputWorkletLoaded(),
    playbackConsumed: playbackConsumedCount,
    audioOutputsReceived: audioOutputsReceivedCount,
    captureStreaming: () => capture.isStreaming()
  };
}
