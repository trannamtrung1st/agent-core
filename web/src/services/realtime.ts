import { HttpTransportType, HubConnection, HubConnectionBuilder } from "@microsoft/signalr";
import { MessagePackHubProtocol } from "@microsoft/signalr-protocol-msgpack";
import { capture } from "../audio/capture";
import { EARLY_AUDIO_MS, MAX_QUEUED_SAMPLES, OutputAudioGate, type OutputAudioFrame } from "../audio/outputAdmission";
import { useChatStore } from "../state/chatStore";
import { applyServerEvent, emptySession, type ServerEvent } from "../state/sessionStore";
import { createSession, endSession, listAgents } from "./api";

let connection: HubConnection | null = null;
let connectionEpoch = 0;
let commandSequence = 0;
let audioFramesSent = 0;
let audioOutputsReceived = 0;
let playbackConsumed = 0;
let playbackSentSamples = 0;
let playbackResponseId: string | null = null;
let playbackStarted = false;
let playbackFinal = false;
let playbackCompletedSent = false;
let progressTimer: number | null = null;
let receiptTimer: number | null = null;
let lastReceiptOffset = 0;
let lastReceiptResponseId: string | null = null;
let disposed = false;
let duckTimer: number | null = null;
let flushing = false;
const stoppedResponses = new Set<string>();
const pendingAudio: OutputAudioFrame[] = [];
const earlyAudio = new Map<string, { frames: OutputAudioFrame[]; timer: number }>();
const outputGate = new OutputAudioGate();
const committedText = new Map<string, number>();
const duckingEnabled = true;

function uuid(): string {
  return crypto.randomUUID();
}

export function audioFramesSentCount(): number {
  return audioFramesSent;
}

export function playbackConsumedCount(): number {
  return playbackConsumed;
}

export function playbackDiagnostics() {
  return {
    consumed: capture.playbackConsumed(),
    queued: capture.playbackQueued(),
    responseId: capture.playbackResponseId() ?? playbackResponseId,
    epoch: capture.playbackEpoch(),
    closed: capture.playbackClosed(),
    rendered: capture.playbackRendered(),
    completedResponses: capture.playbackCompletedResponses(),
    started: playbackStarted,
    final: playbackFinal
  };
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
    attachmentId: type === "session.attach" ? undefined : snapshot.attachmentId,
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
  if (raw.type === "agent.response.started" && raw.responseId) {
    outputGate.markStarted(raw.responseId);
    flushEarlyAudio(raw.responseId);
  }
  if (raw.type === "agent.response.started" && useChatStore.getState().mode === "text" && raw.responseId) {
    lastReceiptOffset = 0;
    lastReceiptResponseId = raw.responseId;
    startReceipts(raw.responseId);
  }
  if ((raw.type === "agent.response.completed" || raw.type === "agent.response.interrupted") && raw.responseId) {
    void sendReceipt(true, raw.responseId);
    stopReceipts();
  }
  if (raw.type === "playback.gain") {
    const gain = typeof raw.payload.gain === "number" ? raw.payload.gain : 1;
    const rampMs = typeof raw.payload.rampMs === "number" ? raw.payload.rampMs : 20;
    applyGain(gain, rampMs);
  }

  if (raw.type === "playback.stop") {
    stopPlayback(raw.responseId ?? undefined);
  }

  if (raw.type === "agent.response.interrupted" && raw.responseId) {
    void interruptPlayback(raw.responseId);
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
  sessionId?: string;
  attachmentId?: string;
  responseId?: string;
  frameSequence?: number;
  sampleOffset?: number;
  data?: unknown;
  isFinal?: boolean;
}): void {
  audioOutputsReceived += 1;
  const responseId = dto.responseId ?? (dto as { ResponseId?: string }).ResponseId;
  if (!responseId || !connection) {
    return;
  }

  const state = useChatStore.getState();
  const frame: OutputAudioFrame = {
    sessionId: dto.sessionId ?? (dto as { SessionId?: string }).SessionId,
    attachmentId: dto.attachmentId ?? (dto as { AttachmentId?: string }).AttachmentId,
    responseId,
    frameSequence: dto.frameSequence ?? (dto as { FrameSequence?: number }).FrameSequence,
    sampleOffset: dto.sampleOffset ?? (dto as { SampleOffset?: number }).SampleOffset,
    data: bytesOf(dto.data ?? (dto as { Data?: unknown }).Data),
    isFinal: Boolean(dto.isFinal ?? (dto as { IsFinal?: boolean }).IsFinal)
  };

  const queuedSamples =
    capture.playbackQueued() +
    pendingAudio.reduce((sum, item) => sum + Math.floor(item.data.length / 2), 0) +
    outputGate.queuedBuffered();
  const decision = outputGate.admit(frame, {
    sessionId: state.sessionId,
    attachmentId: state.attachmentId,
    tombstones: state.tombstones,
    stopped: stoppedResponses,
    queuedSamples
  });
  if (decision === "reject") {
    if (queuedSamples + Math.floor(frame.data.length / 2) + outputGate.queuedBuffered() > MAX_QUEUED_SAMPLES) {
      failOutputOverflow(responseId);
    }
    return;
  }

  if (decision === "buffer") {
    bufferEarlyAudio(frame);
    return;
  }

  if (flushing) {
    pendingAudio.push(frame);
    return;
  }

  enqueueLiveAudio(frame, responseId);
}

function bufferEarlyAudio(frame: OutputAudioFrame): void {
  const responseId = frame.responseId;
  if (!responseId) {
    return;
  }

  const existing = earlyAudio.get(responseId);
  if (existing) {
    existing.frames.push(frame);
    return;
  }

  const timer = window.setTimeout(() => {
    earlyAudio.delete(responseId);
  }, EARLY_AUDIO_MS);
  earlyAudio.set(responseId, { frames: [frame], timer });
}

function flushEarlyAudio(responseId: string): void {
  const buffered = earlyAudio.get(responseId);
  if (!buffered) {
    return;
  }

  window.clearTimeout(buffered.timer);
  earlyAudio.delete(responseId);
  for (const frame of buffered.frames) {
    if (!outputGate.commitBuffered(frame, capture.playbackQueued())) {
      failOutputOverflow(responseId);
      continue;
    }

    if (flushing) {
      pendingAudio.push(frame);
      continue;
    }

    enqueueLiveAudio(frame, responseId);
  }
}

function clearEarlyAudio(): void {
  for (const buffered of earlyAudio.values()) {
    window.clearTimeout(buffered.timer);
  }
  earlyAudio.clear();
}

function failOutputOverflow(responseId: string): void {
  useChatStore.setState({ error: "Audio output exceeded the 2 second queue." });
  if (stoppedResponses.has(responseId) && !playbackStarted) {
    return;
  }

  stoppedResponses.add(responseId);
  playbackStarted = false;
  playbackFinal = false;
  playbackCompletedSent = false;
  playbackSentSamples = 0;
  playbackResponseId = null;
  capture.setPlaybackListener(null);
  capture.setOverflowListener(null);
  void interruptPlayback(responseId);
}

function enqueueLiveAudio(dto: OutputAudioFrame, responseId: string): void {
  const pcm = dto.data;
  const isFinal = Boolean(dto.isFinal);
  if (!playbackStarted || playbackResponseId !== responseId) {
    playbackStarted = true;
    playbackResponseId = responseId;
    playbackFinal = false;
    playbackCompletedSent = false;
    playbackConsumed = 0;
    playbackSentSamples = 0;
    capture.setPlaybackListener((consumed) => {
      playbackConsumed = consumed;
      playbackSentSamples = consumed + capture.playbackQueued();
      maybeCompletePlayback();
    });
    capture.setOverflowListener(() => {
      if (playbackResponseId) {
        failOutputOverflow(playbackResponseId);
      }
    });
    void sendPlayback("PlaybackStarted", "playback.started", responseId, 0);
    startProgress();
  }

  if (!capture.enqueuePlayback(responseId, pcm, isFinal)) {
    failOutputOverflow(responseId);
    return;
  }

  playbackSentSamples = capture.playbackConsumed() + capture.playbackQueued();
  if (isFinal) {
    playbackFinal = true;
    maybeCompletePlayback();
  }
}

function applyGain(gain: number, rampMs: number): void {
  if (!duckingEnabled) {
    return;
  }

  if (duckTimer !== null) {
    window.clearTimeout(duckTimer);
    duckTimer = null;
  }

  capture.setGain(gain, rampMs);
}

function duckLocally(): void {
  if (!duckingEnabled || !playbackStarted) {
    return;
  }

  applyGain(0.2, 20);
  duckTimer = window.setTimeout(() => {
    capture.setGain(1, 20);
    duckTimer = null;
  }, 600);
}

async function interruptPlayback(responseId: string): Promise<void> {
  if (stoppedResponses.has(responseId) && !playbackStarted) {
    return;
  }

  stoppedResponses.add(responseId);
  flushing = true;
  stopProgress();
  if (duckTimer !== null) {
    window.clearTimeout(duckTimer);
    duckTimer = null;
  }

  try {
    const stoppedAt = await capture.flushPlayback(responseId);
    if (playbackResponseId === responseId) {
      playbackStarted = false;
      playbackFinal = false;
      playbackCompletedSent = false;
      playbackResponseId = null;
      playbackConsumed = stoppedAt;
      playbackSentSamples = 0;
      capture.setPlaybackListener(null);
      void sendPlayback("PlaybackStopped", "playback.stopped", responseId, stoppedAt);
    }
  } finally {
    flushing = false;
    const queued = pendingAudio.splice(0, pendingAudio.length);
    for (const item of queued) {
      const id = item.responseId;
      if (!id || stoppedResponses.has(id) || useChatStore.getState().tombstones[id]) {
        continue;
      }

      enqueueLiveAudio(item, id);
    }
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
    playbackSentSamples = consumed + capture.playbackQueued();
    void sendPlayback("PlaybackProgress", "playback.progress", responseId, consumed);
    maybeCompletePlayback();
  }, 100);
}

function maybeCompletePlayback(): void {
  const responseId = playbackResponseId;
  if (!responseId || !playbackFinal || playbackCompletedSent) {
    return;
  }

  if (capture.playbackQueued() > 0 || capture.playbackConsumed() < playbackSentSamples) {
    return;
  }

  const consumed = capture.playbackConsumed();
  playbackCompletedSent = true;
  playbackConsumed = consumed;
  void sendPlayback("PlaybackCompleted", "playback.completed", responseId, consumed);
  stopProgress();
  playbackStarted = false;
  playbackFinal = false;
  playbackResponseId = null;
  playbackSentSamples = 0;
  capture.setPlaybackListener(null);
}

function stopProgress(): void {
  if (progressTimer !== null) {
    window.clearInterval(progressTimer);
    progressTimer = null;
  }
}

function renderedTextOffset(responseId: string | null): number {
  if (!responseId) {
    return 0;
  }

  return committedText.get(responseId) ?? 0;
}

export function reportCommittedEntries(
  entries: Array<{ role: string; responseId: string | null; text: string; status: string }>
): void {
  for (const entry of entries) {
    if (entry.role !== "assistant" || !entry.responseId) {
      continue;
    }

    const next = entry.text.length;
    const previous = committedText.get(entry.responseId) ?? 0;
    if (next > previous) {
      committedText.set(entry.responseId, next);
    }
  }

  const snapshot = useChatStore.getState();
  if (snapshot.mode === "text") {
    void sendReceipt(false);
  }
}

function startReceipts(responseId: string): void {
  stopReceipts();
  receiptTimer = window.setInterval(() => {
    void sendReceipt(false, responseId);
  }, 100);
}

function stopReceipts(): void {
  if (receiptTimer !== null) {
    window.clearInterval(receiptTimer);
    receiptTimer = null;
  }
}

async function sendReceipt(finalRender: boolean, responseId?: string): Promise<void> {
  const snapshot = useChatStore.getState();
  if (snapshot.mode !== "text" || !connection) {
    return;
  }

  const id = responseId ?? snapshot.liveResponseId ?? lastReceiptResponseId;
  if (!id) {
    return;
  }

  const offset = renderedTextOffset(id);
  if (!finalRender && offset === lastReceiptOffset && lastReceiptResponseId === id) {
    return;
  }

  lastReceiptOffset = offset;
  lastReceiptResponseId = id;
  commandSequence += 1;
  await invoke("ResponseReceived", "response.received", { textEndExclusive: offset }, commandSequence, id);
}

async function sendPlayback(method: string, type: string, responseId: string, consumed: number): Promise<void> {
  commandSequence += 1;
  await invoke(method, type, { consumedSamples: consumed, textEndExclusive: renderedTextOffset(responseId) }, commandSequence, responseId);
}

function abortPlayback(): void {
  stopProgress();
  stopReceipts();
  flushing = false;
  pendingAudio.length = 0;
  clearEarlyAudio();
  outputGate.reset();
  playbackStarted = false;
  playbackFinal = false;
  playbackCompletedSent = false;
  playbackResponseId = null;
  playbackSentSamples = 0;
  playbackConsumed = 0;
  capture.setPlaybackListener(null);
  void capture.flushPlayback("");
}

function stopPlayback(responseId?: string): void {
  const id = responseId ?? playbackResponseId ?? "";
  if (id) {
    void interruptPlayback(id);
    return;
  }

  abortPlayback();
}

function publishCaptureLive(): void {
  const state = useChatStore.getState();
  const live = capture.isPrepared() && (capture.isStreaming() || state.muted);
  if (state.captureLive !== live) {
    useChatStore.setState({ captureLive: live });
  }
}

function syncCapture(): void {
  const state = useChatStore.getState();
  if (state.connection !== "ready") {
    capture.release();
    publishCaptureLive();
    return;
  }

  if (state.mode === "voice" && state.streamId && connection) {
    if (state.muted) {
      if (capture.isStreaming()) {
        void capture.muteInput();
      }

      publishCaptureLive();
      return;
    }

    if (!capture.isPrepared()) {
      publishCaptureLive();
      return;
    }

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
          duckLocally();
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

    publishCaptureLive();
    return;
  }

  if (state.pendingMode === "voice" || state.preflightReady) {
    publishCaptureLive();
    return;
  }

  capture.release();
  publishCaptureLive();
}

async function startConnection(sessionId: string): Promise<void> {
  await stopConnection();
  commandSequence = 0;
  audioFramesSent = 0;
  audioOutputsReceived = 0;
  const epoch = ++connectionEpoch;
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
    if (epoch !== connectionEpoch) {
      return;
    }

    abortPlayback();
    capture.release();
    useChatStore.setState({
      connection: "reconnecting",
      pendingMode: null,
      preflightReady: false,
      captureLive: false,
      attachmentId: null
    });
  });
  connection.onreconnected(async () => {
    if (epoch !== connectionEpoch) {
      return;
    }

    commandSequence = 0;
    await invoke("Attach", "session.attach", { lastServerSequence: useChatStore.getState().lastServerSequence || null }, 0);
  });
  connection.onclose(() => {
    if (epoch !== connectionEpoch) {
      return;
    }

    abortPlayback();
    capture.release();
    if (!disposed) {
      useChatStore.setState({
        connection: "reconnecting",
        pendingMode: null,
        preflightReady: false,
        captureLive: false,
        attachmentId: null
      });
    }
  });
  useChatStore.setState({
    connection: "connecting",
    sessionId,
    attachmentId: null,
    pendingMode: null,
    preflightReady: false,
    captureLive: false,
    lastServerSequence: 0
  });
  await connection.start();
  const ack = await invoke("Attach", "session.attach", { lastServerSequence: null }, 0);
  if (!ack?.accepted) {
    useChatStore.setState({ error: ack?.error?.message ?? "Attach failed.", connection: "failed" });
  }
}

async function stopConnection(): Promise<void> {
  abortPlayback();
  capture.release();
  connectionEpoch += 1;
  useChatStore.setState({ captureLive: false });
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
      error: microphoneError(error)
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
  useChatStore.setState({ preflightReady: false, muted: false });
  commandSequence += 1;
  await invoke("SetMode", "session.mode.set", { mode: "text" }, commandSequence);
}

export async function setMuted(muted: boolean): Promise<void> {
  if (muted) {
    await capture.muteInput();
    publishCaptureLive();
  }

  commandSequence += 1;
  const ack = await invoke("SetMuted", "session.mute", { muted }, commandSequence);
  if (!ack?.accepted) {
    useChatStore.setState({ error: ack?.error?.message ?? "Mute failed." });
    return;
  }

  if (!muted) {
    syncCapture();
  }
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
  stopReceipts();
  capture.release();
  useChatStore.setState({
    ...emptySession(),
    agents: snapshot.agents,
    selectedAgentId: snapshot.selectedAgentId
  });
  disposed = false;
}

function microphoneError(error: unknown): string {
  if (error instanceof DOMException && error.name === "NotAllowedError") {
    return "Microphone permission was denied. Enable the microphone or continue in text.";
  }

  if (error instanceof Error && /AudioWorklet/i.test(error.message)) {
    return "This browser cannot start voice capture. Continue in text.";
  }

  return error instanceof Error ? error.message : "Microphone preflight failed.";
}

if (typeof window !== "undefined") {
  window.__agentCore = {
    audioFramesSent: audioFramesSentCount,
    disconnect: async () => {
      abortPlayback();
      capture.release();
      await stopConnection();
      useChatStore.setState({
        connection: "reconnecting",
        pendingMode: null,
        preflightReady: false,
        captureLive: false,
        attachmentId: null
      });
    },
    reconnect: async () => {
      const sessionId = useChatStore.getState().sessionId;
      if (sessionId) {
        await startConnection(sessionId);
      }
    },
    capturePrepared: () => capture.isPrepared() || capture.isStreaming(),
    workletLoaded: () => capture.workletLoaded(),
    outputWorkletLoaded: () => capture.outputWorkletLoaded(),
    playbackConsumed: playbackConsumedCount,
    playbackDiagnostics,
    audioOutputsReceived: audioOutputsReceivedCount,
    captureStreaming: () => capture.isStreaming(),
    flushing: () => flushing
  };
}
