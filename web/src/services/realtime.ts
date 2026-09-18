import { HttpTransportType, HubConnection, HubConnectionBuilder } from "@microsoft/signalr";
import { MessagePackHubProtocol } from "@microsoft/signalr-protocol-msgpack";
import { capture } from "../audio/capture";
import { EARLY_AUDIO_MS, OutputAudioGate, type OutputAudioFrame } from "../audio/outputAdmission";
import { applyServerEvent, emptySession, hasControlSequenceGap, historyFromPayload, isReadonlySession, useSessionStore, type HistoryAttachment, type HistoryEntry, type PendingSendItem, type ServerEvent } from "../state/sessionStore";
import { sessionErrorFromMessage, sessionErrorFromWire, type WireError } from "../features/chat/sessionError";
import { parseSessionIdFromPath, sameSessionId, syncBrowserSessionPath } from "../app/sessionRoute";
import { createSession, clearOwnerCapability, endSession, ensureOwnerCapability, getHealth, getSession, listAgents, listSessionMessages, reopenSession, type HistoryPage } from "./api";
import {
  abortPendingAttachment,
  listAttachments,
  pendingFile,
  releaseAllPendingFiles,
  releasePendingFile,
  retainPendingFile,
  stageAttachments,
  uploadAttachment,
  type PendingAttachment
} from "./attachments";
import { catalogShell, refreshCatalog } from "./catalog";
import { requiresExplicitResume } from "./sessionPauseSemantics";
import { createBrowserSpeechRecognizer } from "../speech/browserSpeechRecognizer";
import { createBrowserSpeechSynthesizer } from "../speech/browserSpeechSynthesizer";
import { createClientSpeechPlayer, type ClientSpeechPlayer } from "../speech/clientSpeechPlayer";
import type { ClientSpeechEvidence } from "../speech/clientSpeechRecognizer";
import type { ClientSpeechSynthesizer } from "../speech/clientSpeechSynthesizer";
import { clientRecognitionSupported, clientSynthesisSupported, speechTestSeam } from "../speech/clientSpeechSupport";
import { ClientTranscriptLifecycle } from "../speech/clientTranscriptLifecycle";
import { createFakeSpeechRecognizer, createFakeSpeechSynthesizer, type FakeSpeechRecognizer, type FakeSpeechSynthesizer } from "../speech/fakeSpeechAdapters";
import { speechTransport } from "../speech/speechTransport";
import { voiceControlEnabled } from "../speech/voiceEnablement";

let connection: HubConnection | null = null;
let connectionEpoch = 0;
let commandSequence = 0;
let clientSpeechPlayer: ClientSpeechPlayer | null = null;
let transcriptLife: ClientTranscriptLifecycle | null = null;
let clientTranscriptHeldForAgent = false;
let injectedRecognizer: FakeSpeechRecognizer | null = null;
let injectedSynthesizer: FakeSpeechSynthesizer | null = null;
let transcriptStart: Promise<void> | null = null;
let speechEvidenceChain: Promise<void> = Promise.resolve();
let speechEvidenceAttempts = 0;
let audioFramesSent = 0;
let audioOutputsReceived = 0;
let playbackConsumed = 0;
let playbackResponseId: string | null = null;
let playbackStarted = false;
let playbackFinal = false;
let playbackWorkletComplete = false;
let playbackCompletedSent = false;
let progressTimer: number | null = null;
let receiptTimer: number | null = null;
let lastReceiptOffset = 0;
let lastReceiptResponseId: string | null = null;
let disposed = false;
let duckTimer: number | null = null;
let flushing = false;
let playbackInterrupt = Promise.resolve();
const stoppedResponses = new Set<string>();
const pendingAudio: OutputAudioFrame[] = [];
const earlyAudio = new Map<string, { frames: OutputAudioFrame[]; timer: number }>();
const outputGate = new OutputAudioGate();
const committedText = new Map<string, number>();
const committedBlocks = new Map<string, string[]>();
const duckingEnabled = true;
let captureStreamId: string | null = null;
let voiceRequest: Promise<void> | null = null;
let voiceEpoch = 0;
let sendRequest: Promise<void> | null = null;
const pendingStops = new Map<string, Promise<void>>();
let startRequest: Promise<boolean> | null = null;

const MAX_PENDING_SEND_ITEMS = 20;
const MAX_PENDING_SEND_TEXT_UNITS = 24000;
const MAX_USER_TEXT_UNITS = 8000;

function uuid(): string {
  return crypto.randomUUID();
}

function sessionFailurePatch(
  message: string,
  options: {
    fatal?: boolean;
    wire?: WireError | null;
    category?: string;
    code?: string;
    retryAfterMs?: number | null;
  } = {}
) {
  const view = options.wire
    ? sessionErrorFromWire(options.wire, message)
    : sessionErrorFromMessage(message, {
        fatal: options.fatal,
        category: options.category,
        code: options.code,
        retryAfterMs: options.retryAfterMs
      });
  const fatal = options.fatal ?? view.fatal;
  return {
    error: view.message,
    sessionError: view,
    errorFatal: fatal,
    errorHoldSequence: fatal ? 0 : useSessionStore.getState().lastServerSequence
  };
}

function clearSessionFailure() {
  return { error: null as string | null, sessionError: null, errorFatal: false };
}

function syncClientTranscriptBlockedFromLifecycle(): void {
  const blocked = Boolean(transcriptLife?.isBlocked());
  if (useSessionStore.getState().clientTranscriptBlocked !== blocked) {
    useSessionStore.setState({ clientTranscriptBlocked: blocked });
  }
}

export function audioFramesSentCount(): number {
  return audioFramesSent;
}

export function playbackConsumedCount(): number {
  return playbackConsumed;
}

export function playbackDiagnostics() {
  const rendered = capture.playbackRendered();
  let fallbackResponseId: string | null = null;
  let bestRendered = 0;
  for (const [id, count] of Object.entries(rendered)) {
    if (count > bestRendered) {
      bestRendered = count;
      fallbackResponseId = id;
    }
  }

  return {
    consumed: capture.playbackConsumed(),
    queued: capture.playbackQueued(),
    responseId: capture.playbackResponseId() ?? playbackResponseId ?? fallbackResponseId,
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
  useSessionStore.setState({ draft });
}

export function selectAgent(agentId: string): void {
  useSessionStore.setState({ selectedAgentId: agentId });
}

function command(
  type: string,
  payload: Record<string, unknown>,
  sequence: number,
  responseId: string | null = null,
  eventId: string = uuid()
) {
  const snapshot = useSessionStore.getState();
  const payloadWithOwner =
    type === "session.attach"
      ? { ...payload, ownerCapability: window.localStorage.getItem("agent-core.owner-capability") }
      : payload;
  return {
    protocolVersion: 1,
    sessionId: snapshot.sessionId,
    eventId,
    sequence,
    timestamp: new Date().toISOString(),
    responseId,
    attachmentId: type === "session.attach" ? undefined : snapshot.attachmentId,
    type,
    payload: payloadWithOwner
  };
}

async function invoke(
  method: string,
  type: string,
  payload: Record<string, unknown>,
  sequence: number,
  responseId: string | null = null,
  eventId?: string
) {
  if (!connection) {
    throw new Error("Not connected.");
  }

  return connection.invoke(method, command(type, payload, sequence, responseId, eventId)) as Promise<{
    accepted?: boolean;
    error?: WireError;
  }>;
}

function bindSpeechTransports(): void {
  const snapshot = useSessionStore.getState();
  speechTransport.setActiveInputTransport(
    snapshot.sttTransport === "clientTranscript" || snapshot.sttTransport === "serverAudio"
      ? snapshot.sttTransport
      : null
  );
  speechTransport.setActiveOutputTransport(
    snapshot.ttsTransport === "clientSpeech" || snapshot.ttsTransport === "serverAudio"
      ? snapshot.ttsTransport
      : null
  );
}

function ensureSpeechAdapters(): void {
  const seam = speechTestSeam();
  if (!speechTransport.inputRecognizerAdapterId()) {
    if (seam.recognizer) {
      speechTransport.setInputRecognizer(seam.recognizer);
      injectedRecognizer = seam.recognizer instanceof Object && "emit" in seam.recognizer
        ? seam.recognizer as FakeSpeechRecognizer
        : injectedRecognizer;
    } else if (seam.fakeRecognizer) {
      injectedRecognizer = createFakeSpeechRecognizer();
      speechTransport.setInputRecognizer(injectedRecognizer);
    } else {
      speechTransport.setInputRecognizer(createBrowserSpeechRecognizer());
    }
  }

  if (!speechTransport.outputSynthesizerAdapterId()) {
    if (seam.synthesizer) {
      speechTransport.setOutputSynthesizer(seam.synthesizer);
      injectedSynthesizer = "armHold" in seam.synthesizer ? seam.synthesizer as FakeSpeechSynthesizer : injectedSynthesizer;
    } else if (seam.fakeSynthesizer) {
      injectedSynthesizer = createFakeSpeechSynthesizer();
      speechTransport.setOutputSynthesizer(injectedSynthesizer);
    } else {
      speechTransport.setOutputSynthesizer(createBrowserSpeechSynthesizer());
    }
  }

  bindSpeechTransports();
}

function transportSynthesizer(): ClientSpeechSynthesizer {
  ensureSpeechAdapters();
  return {
    adapterId: speechTransport.outputSynthesizerAdapterId() ?? "fake",
    listVoices: () => [],
    resolveVoice: (hint) => speechTransport.resolveOutputVoice(hint),
    speak: (request, listener) => speechTransport.speakOutput(request, listener),
    cancel: () => speechTransport.cancelOutput()
  };
}

function sendClientSpeechAck(
  responseId: string,
  report: { kind: "started" | "progress" | "completed" | "stopped"; textEndExclusive: number }
): void {
  const method =
    report.kind === "started"
      ? "PlaybackStarted"
      : report.kind === "progress"
        ? "PlaybackProgress"
        : report.kind === "completed"
          ? "PlaybackCompleted"
          : "PlaybackStopped";
  const type = `playback.${report.kind}`;
  const send = sendPlayback(method, type, responseId, 0, report.textEndExclusive);
  if (report.kind === "completed" || report.kind === "stopped") {
    void send.finally(() => {
      scheduleClientTranscriptResumeAfterPlaybackIdle();
    });
    return;
  }

  void send;
}

function ensureClientSpeechPlayer(): ClientSpeechPlayer {
  if (!clientSpeechPlayer) {
    clientSpeechPlayer = createClientSpeechPlayer(transportSynthesizer(), sendClientSpeechAck);
  }

  return clientSpeechPlayer;
}

function sendSpeechEvidence(evidence: ClientSpeechEvidence): void {
  const snapshot = useSessionStore.getState();
  if (!connection || snapshot.sttTransport !== "clientTranscript" || snapshot.mode !== "voice") {
    return;
  }

  const payload: Record<string, unknown> = {
    kind: evidence.kind,
    utteranceId: evidence.utteranceId
  };
  if (evidence.revision != null) {
    payload.revision = evidence.revision;
  }
  if (evidence.text != null) {
    payload.text = evidence.text;
  }
  if (evidence.confidence != null) {
    payload.confidence = evidence.confidence;
  }
  if (evidence.activityScore != null) {
    payload.activityScore = evidence.activityScore;
  }
  if (evidence.durationMs != null) {
    payload.durationMs = evidence.durationMs;
  }

  speechEvidenceAttempts += 1;
  commandSequence += 1;
  const sequence = commandSequence;
  speechEvidenceChain = speechEvidenceChain.then(async () => {
    const ack = await invoke("SpeechEvidence", "client.speech.evidence", payload, sequence);
    if (ack?.accepted) {
      return;
    }

    useSessionStore.setState({
      error: ack?.error?.message ?? "Speech evidence was not accepted.",
      sessionError: sessionErrorFromWire(ack?.error, ack?.error?.message ?? "Speech evidence was not accepted."),
      errorFatal: false
    });
  }).catch((error: unknown) => {
    useSessionStore.setState({
      ...sessionFailurePatch(error instanceof Error ? error.message : "Speech evidence failed.", {
        category: "Speech",
        code: "SpeechEvidenceFailed"
      })
    });
  });
}

function clearLiveUserTranscript(): void {
  if (useSessionStore.getState().liveUserTranscript !== null) {
    useSessionStore.setState({ liveUserTranscript: null });
  }
}

function publishVoiceInputHeldForAgentOutput(held: boolean): void {
  if (useSessionStore.getState().voiceInputHeldForAgentOutput !== held) {
    useSessionStore.setState({ voiceInputHeldForAgentOutput: held });
  }
}

function clearVoiceInputHeldForAgentOutput(): void {
  clientTranscriptHeldForAgent = false;
  publishVoiceInputHeldForAgentOutput(false);
}

async function suspendClientTranscriptForAgentOutput(): Promise<void> {
  const state = useSessionStore.getState();
  if (state.sttTransport !== "clientTranscript" || state.mode !== "voice") {
    return;
  }

  clientTranscriptHeldForAgent = true;
  publishVoiceInputHeldForAgentOutput(true);
  clearLiveUserTranscript();
  await ensureTranscriptLife().suspendForAgentOutput();
  publishCaptureLive();
}

async function tryResumeClientTranscriptAfterAgentOutput(): Promise<void> {
  if (!clientTranscriptHeldForAgent) {
    return;
  }

  const state = useSessionStore.getState();
  if (state.sttTransport !== "clientTranscript" || state.mode !== "voice") {
    clearVoiceInputHeldForAgentOutput();
    return;
  }

  if (state.liveResponseId != null || voicePlaybackHoldActive()) {
    return;
  }

  clientTranscriptHeldForAgent = false;
  publishVoiceInputHeldForAgentOutput(false);

  if (!state.muted) {
    await ensureTranscriptLife().resumeAfterAgentOutput();
    syncCapture();
  } else {
    publishCaptureLive();
  }
}

function scheduleClientTranscriptResumeAfterPlaybackIdle(): void {
  void tryResumeClientTranscriptAfterAgentOutput();
}

function ensureTranscriptLife(): ClientTranscriptLifecycle {
  ensureSpeechAdapters();
  if (!transcriptLife) {
    transcriptLife = new ClientTranscriptLifecycle(
      speechTransport,
      sendSpeechEvidence,
      (error) => {
        useSessionStore.setState({
          error: error.message,
          sessionError: error,
          errorFatal: false
        });
        clearLiveUserTranscript();
        syncClientTranscriptBlockedFromLifecycle();
        publishCaptureLive();
      },
      () => {
        syncClientTranscriptBlockedFromLifecycle();
        publishCaptureLive();
      },
      () => {
        clearLiveUserTranscript();
      }
    );
  }

  return transcriptLife;
}

async function releaseClientSpeech(): Promise<void> {
  clearVoiceInputHeldForAgentOutput();
  await clientSpeechPlayer?.reset();
  await transcriptLife?.disconnect();
  syncClientTranscriptBlockedFromLifecycle();
}

function sessionVoiceEnabled(): boolean {
  const snapshot = useSessionStore.getState();
  return voiceControlEnabled({
    voiceAvailable: snapshot.voiceAvailable,
    inputTransport: snapshot.sttTransport,
    outputTransport: snapshot.ttsTransport,
    recognitionSupported: clientRecognitionSupported(),
    synthesisSupported: clientSynthesisSupported()
  });
}

const RECONNECT_BUDGET_MS = 60_000;
const RECONNECT_DELAYS_MS = [0, 2000, 5000, 10000];

let reconnectBudgetStarted = 0;
let attachLoop = 0;
let pendingUserText: {
  eventId: string;
  text: string;
  attachmentIds: string[];
  pendingAttachments: PendingAttachment[];
  queueLocalId?: string;
} | null = null;

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => {
    window.setTimeout(resolve, ms);
  });
}

function beginReconnectBudget(): void {
  reconnectBudgetStarted = Date.now();
}

function reconnectBudgetRemaining(): number {
  if (reconnectBudgetStarted === 0) {
    return RECONNECT_BUDGET_MS;
  }

  return RECONNECT_BUDGET_MS - (Date.now() - reconnectBudgetStarted);
}

function dropLiveTransport(connection: "reconnecting" | "failed"): void {
  abortPlayback();
  void releaseClientSpeech();
  capture.release();
  clearVoiceInputHeldForAgentOutput();
  useSessionStore.setState({
    connection,
    pendingMode: null,
    preflightReady: false,
    captureLive: false,
    attachmentId: null,
    ...(connection === "reconnecting"
      ? { error: null, sessionError: null, errorFatal: false, errorHoldSequence: 0 }
      : {})
  });
}

function markConnectionFailed(message: string, options?: { force?: boolean }): void {
  const latest = useSessionStore.getState();
  if (
    !options?.force
    && (latest.connection === "reconnecting" || latest.connection === "connecting")
  ) {
    return;
  }

  for (const item of latest.pendingSendQueue) {
    for (const attachment of item.attachments) {
      releasePendingFile(attachment.localId);
    }
  }
  abortPlayback();
  void releaseClientSpeech();
  capture.release();
  clearVoiceInputHeldForAgentOutput();
  useSessionStore.setState({
    ...sessionFailurePatch(message, { category: "Transport", code: "ReconnectFailed" }),
    errorHoldSequence: latest.lastServerSequence,
    connection: "failed",
    pendingMode: null,
    preflightReady: false,
    captureLive: false,
    attachmentId: null,
    pendingSendQueue: []
  });
}

function isTransientAttachError(ack: { error?: { code?: string } } | null | undefined): boolean {
  const code = ack?.error?.code;
  return code === "SessionInUse" || code === "SessionBusy";
}

async function attachWithBusyRetry(lastServerSequence: number | null): Promise<boolean> {
  const loop = attachLoop;
  let delayIndex = 0;
  let retriedOwnerCapability = false;
  while (loop === attachLoop && connection) {
    try {
      commandSequence = 0;
      const ack = await invoke(
        "Attach",
        "session.attach",
        { lastServerSequence: lastServerSequence || null },
        0
      );
      if (loop !== attachLoop) {
        return false;
      }

      if (ack?.accepted) {
        reconnectBudgetStarted = 0;
        return true;
      }

      if (ack?.error?.code === "Unauthorized" && !retriedOwnerCapability) {
        retriedOwnerCapability = true;
        clearOwnerCapability();
        await ensureOwnerCapability();
        continue;
      }

      if (ack?.error?.code === "NotFound") {
        markConnectionFailed("This conversation is no longer available.", { force: true });
        return false;
      }

      if (!isTransientAttachError(ack) || reconnectBudgetRemaining() <= 0) {
        const message =
          reconnectBudgetRemaining() <= 0
            ? "Connection lost. Retry to continue."
            : ack?.error?.message ?? "Reconnect failed.";
        markConnectionFailed(message, { force: true });
        return false;
      }

      dropLiveTransport("reconnecting");
      const wait = Math.min(
        ack?.error?.retryAfterMs ?? RECONNECT_DELAYS_MS[Math.min(delayIndex, RECONNECT_DELAYS_MS.length - 1)],
        Math.max(0, reconnectBudgetRemaining())
      );
      delayIndex += 1;
      if (wait > 0) {
        await delay(wait);
      }
    } catch (error) {
      if (loop !== attachLoop) {
        return false;
      }

      markConnectionFailed(error instanceof Error ? error.message : "Reconnect failed.", { force: true });
      return false;
    }
  }

  return false;
}

function handleHubClosed(): void {
  attachLoop += 1;
  if (disposed) {
    return;
  }

  void recoverFromTransientDisconnect();
}

async function recoverFromTransientDisconnect(): Promise<void> {
  const snapshot = useSessionStore.getState();
  if (!connection || !snapshot.sessionId) {
    markConnectionFailed("Connection lost. Retry to continue.", { force: true });
    return;
  }

  beginReconnectBudget();
  dropLiveTransport("reconnecting");
  try {
    await connection.stop();
  } catch {
    // ignored
  }

  try {
    await connection.start();
    const attached = await attachWithBusyRetry(snapshot.lastServerSequence || null);
    if (!attached && useSessionStore.getState().connection === "reconnecting") {
      markConnectionFailed("Connection lost. Retry to continue.", { force: true });
    }
  } catch (error) {
    markConnectionFailed(error instanceof Error ? error.message : "Connection lost. Retry to continue.", { force: true });
  }
}

async function recoverFromSequenceGap(): Promise<void> {
  const snapshot = useSessionStore.getState();
  if (!connection || !snapshot.sessionId) {
    return;
  }

  beginReconnectBudget();
  dropLiveTransport("reconnecting");
  try {
    await connection.stop();
  } catch {
    // ignored
  }

  try {
    await connection.start();
    const attached = await attachWithBusyRetry(snapshot.lastServerSequence || null);
    if (!attached && useSessionStore.getState().connection === "reconnecting") {
      markConnectionFailed("Connection lost. Retry to continue.", { force: true });
    }
  } catch (error) {
    markConnectionFailed(error instanceof Error ? error.message : "Reconnect failed.", { force: true });
  }
}

function handleEvent(raw: ServerEvent): void {
  if (!connection || isReadonlySession(useSessionStore.getState())) {
    return;
  }

  const prior = useSessionStore.getState();
  if (hasControlSequenceGap(prior, raw)) {
    abortPlayback();
    useSessionStore.setState(applyServerEvent(prior, raw));
    void recoverFromSequenceGap();
    return;
  }

  const next = applyServerEvent(prior, raw);
  if (
    (prior.ttsTransport === "clientSpeech" || next.ttsTransport === "clientSpeech")
    && raw.responseId
    && (raw.type === "speech.output.segment" || raw.type === "speech.output.completed")
  ) {
    bindSpeechTransports();
    if (raw.type === "speech.output.segment") {
      ensureClientSpeechPlayer().enqueue({
        responseId: raw.responseId,
        segmentIndex: asEventNumber(raw.payload.segmentIndex),
        textStart: asEventNumber(raw.payload.textStart),
        text: String(raw.payload.text ?? ""),
        voiceHint: typeof raw.payload.voiceHint === "string" ? raw.payload.voiceHint : undefined,
        language: typeof raw.payload.language === "string" ? raw.payload.language : undefined,
        speakingRate: typeof raw.payload.speakingRate === "number" ? raw.payload.speakingRate : undefined
      });
    } else {
      ensureClientSpeechPlayer().markOutputCompleted(raw.responseId, asEventNumber(raw.payload.textEndExclusive));
      void tryResumeClientTranscriptAfterAgentOutput();
    }
  }

  if (next === prior) {
    return;
  }

  useSessionStore.setState(next);
  if (raw.type === "session.ready") {
    bindSpeechTransports();
    reconcilePendingUserText(next.entries);
    void hydrateBoundAttachments(next.sessionId);
    void hydrateActiveHistory(next.sessionId, next.entries);
  }
  if (raw.type === "agent.response.completed" && String(raw.payload.status ?? "completed") === "completed") {
    void maybeAutoDispatchQueueHead();
  }
  if (raw.type === "session.state.changed" && String(raw.payload.status ?? "") === "paused") {
    void stopConnection();
    stopReceipts();
    capture.release();
    void releaseClientSpeech();
  }
  if (raw.type === "transcript.final") {
    void hydrateBoundAttachments(next.sessionId);
  }
  if (raw.type === "agent.response.started" && raw.responseId) {
    outputGate.markStarted(raw.responseId);
    flushEarlyAudio(raw.responseId);
    if (useSessionStore.getState().sttTransport === "clientTranscript") {
      void suspendClientTranscriptForAgentOutput();
    }
  }
  if (raw.type === "agent.response.started" && useSessionStore.getState().mode === "text" && raw.responseId) {
    lastReceiptOffset = 0;
    lastReceiptResponseId = raw.responseId;
    startReceipts(raw.responseId);
  }
  if ((raw.type === "agent.response.completed" || raw.type === "agent.response.interrupted") && raw.responseId) {
    void sendReceipt(true, raw.responseId);
    stopReceipts();
    void tryResumeClientTranscriptAfterAgentOutput();
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

function asEventNumber(value: unknown): number {
  return typeof value === "number" ? value : Number(value ?? 0);
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
  if (!responseId || !connection || isReadonlySession(useSessionStore.getState())) {
    return;
  }

  if (useSessionStore.getState().ttsTransport === "clientSpeech") {
    return;
  }

  const state = useSessionStore.getState();
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
    pendingAudio.reduce((sum, item) => sum + Math.floor(item.data.length / 2), 0);
  const decision = outputGate.admit(frame, {
    sessionId: state.sessionId,
    attachmentId: state.attachmentId,
    tombstones: state.tombstones,
    stopped: stoppedResponses,
    queuedSamples
  });
  if (decision === "overflow") {
    failOutputOverflow(responseId);
    return;
  }

  if (decision === "reject") {
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
    stoppedResponses.add(responseId);
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
      break;
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
  const latest = useSessionStore.getState();
  useSessionStore.setState({
    error: "Audio output exceeded the 2 second queue.",
    errorFatal: false,
    errorHoldSequence: latest.lastServerSequence
  });
  if (stoppedResponses.has(responseId) && !playbackStarted) {
    return;
  }

  stoppedResponses.add(responseId);
  outputGate.drop(responseId);
  const buffered = earlyAudio.get(responseId);
  if (buffered) {
    window.clearTimeout(buffered.timer);
    earlyAudio.delete(responseId);
  }

  capture.setOverflowListener(null);
  void interruptPlayback(responseId);
}

function enqueueLiveAudio(dto: OutputAudioFrame, responseId: string): void {
  const pcm = dto.data;
  const isFinal = Boolean(dto.isFinal);
  if (!playbackStarted || playbackResponseId !== responseId) {
    playbackStarted = true;
    playbackResponseId = responseId;
    syncVoicePlaybackResponseId(responseId);
    playbackFinal = false;
    playbackWorkletComplete = false;
    playbackCompletedSent = false;
    playbackConsumed = 0;
    capture.setPlaybackListener((consumed) => {
      playbackConsumed = consumed;
      syncVoicePlaybackFromCapture();
    });
    capture.setPlaybackCompleteListener((completedResponseId, consumed) => {
      if (playbackResponseId !== completedResponseId) {
        return;
      }

      playbackWorkletComplete = true;
      playbackConsumed = consumed;
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

  playbackConsumed = capture.playbackConsumed();
  syncVoicePlaybackFromCapture();
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

async function stopVoicePlayback(responseId: string): Promise<void> {
  const epochBefore = capture.playbackEpoch();
  const target = capture.playbackResponseId() ?? playbackResponseId ?? responseId;
  if (target) {
    await interruptPlayback(target);
  }

  if (capture.playbackEpoch() === epochBefore && (capture.playbackQueued() > 0 || !capture.playbackClosed())) {
    abortPlayback();
    await playbackInterrupt;
  } else if (!target) {
    abortPlayback();
    await playbackInterrupt;
  }

  syncVoicePlaybackFromCapture();
}

async function interruptPlayback(responseId: string): Promise<void> {
  if (useSessionStore.getState().ttsTransport === "clientSpeech") {
    await ensureClientSpeechPlayer().cancel(responseId);
    return;
  }

  const run = async () => {
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
        playbackWorkletComplete = false;
        playbackCompletedSent = false;
        playbackResponseId = null;
        syncVoicePlaybackResponseId(null);
        playbackConsumed = stoppedAt;
        capture.setPlaybackListener(null);
        capture.setPlaybackCompleteListener(null);
        void sendPlayback("PlaybackStopped", "playback.stopped", responseId, stoppedAt);
      } else if (useSessionStore.getState().voicePlaybackResponseId === responseId) {
        syncVoicePlaybackResponseId(null);
      }
    } finally {
      flushing = false;
      const queued = pendingAudio.splice(0, pendingAudio.length);
      for (const item of queued) {
        const id = item.responseId;
        if (!id || stoppedResponses.has(id) || useSessionStore.getState().tombstones[id]) {
          continue;
        }

        enqueueLiveAudio(item, id);
      }

      scheduleClientTranscriptResumeAfterPlaybackIdle();
    }
  };

  playbackInterrupt = playbackInterrupt.then(run, run);
  return playbackInterrupt;
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
  }, 100);
}

function maybeCompletePlayback(): void {
  const responseId = playbackResponseId;
  if (!responseId || !playbackFinal || !playbackWorkletComplete || playbackCompletedSent) {
    return;
  }

  const consumed = capture.playbackConsumed();
  playbackCompletedSent = true;
  playbackConsumed = consumed;
  void sendPlayback("PlaybackCompleted", "playback.completed", responseId, consumed);
  stopProgress();
  playbackStarted = false;
  playbackFinal = false;
  playbackWorkletComplete = false;
  playbackResponseId = null;
  syncVoicePlaybackResponseId(null);
  capture.setPlaybackListener(null);
  capture.setPlaybackCompleteListener(null);
  scheduleClientTranscriptResumeAfterPlaybackIdle();
}

function stopProgress(): void {
  if (progressTimer !== null) {
    window.clearInterval(progressTimer);
    progressTimer = null;
  }
}

function renderedBlockIds(responseId: string | null): string[] {
  if (!responseId) {
    return [];
  }

  return committedBlocks.get(responseId) ?? [];
}

function renderedTextOffset(responseId: string | null): number {
  if (!responseId) {
    return 0;
  }

  return committedText.get(responseId) ?? 0;
}

export function reportCommittedEntries(
  entries: Array<{
    role: string;
    responseId: string | null;
    text: string;
    status: string;
    blocks?: Array<{ blockId: string }>;
  }>
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

    if (entry.blocks && entry.blocks.length > 0) {
      committedBlocks.set(
        entry.responseId,
        entry.blocks.map((block) => block.blockId)
      );
    }
  }

  const snapshot = useSessionStore.getState();
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
  const snapshot = useSessionStore.getState();
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
  await invoke(
    "ResponseReceived",
    "response.received",
    { textEndExclusive: offset, blockIds: renderedBlockIds(id) },
    commandSequence,
    id
  );
}

async function sendPlayback(method: string, type: string, responseId: string, consumed: number, textEndExclusive?: number): Promise<void> {
  commandSequence += 1;
  await invoke(
    method,
    type,
    {
      consumedSamples: consumed,
      textEndExclusive: textEndExclusive ?? renderedTextOffset(responseId)
    },
    commandSequence,
    responseId
  );
}

function abortPlayback(): void {
  stopProgress();
  stopReceipts();
  flushing = true;
  pendingAudio.length = 0;
  clearEarlyAudio();
  outputGate.reset();
  captureStreamId = null;
  playbackStarted = false;
  playbackFinal = false;
  playbackWorkletComplete = false;
  playbackCompletedSent = false;
  playbackResponseId = null;
  syncVoicePlaybackResponseId(null);
  playbackConsumed = 0;
  capture.setPlaybackListener(null);
  capture.setPlaybackCompleteListener(null);
  void clientSpeechPlayer?.reset();
  playbackInterrupt = playbackInterrupt.then(
    async () => {
      await capture.flushPlayback("");
    },
    async () => {
      await capture.flushPlayback("");
    }
  ).finally(() => {
    flushing = false;
    scheduleClientTranscriptResumeAfterPlaybackIdle();
  });
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
  const state = useSessionStore.getState();
  if (state.sttTransport === "clientTranscript") {
    syncClientTranscriptBlockedFromLifecycle();
  }

  if (state.sttTransport === "clientTranscript" && state.mode === "voice") {
    const live =
      !state.muted
      && Boolean(transcriptLife?.isListening())
      && !transcriptLife?.isBlocked();
    if (state.captureLive !== live) {
      useSessionStore.setState({ captureLive: live });
    }

    return;
  }

  const live = capture.isPrepared() && (capture.isStreaming() || state.muted || state.mode === "voice");
  if (state.captureLive !== live) {
    useSessionStore.setState({ captureLive: live });
  }
}

function syncVoicePlaybackResponseId(responseId: string | null): void {
  if (useSessionStore.getState().voicePlaybackResponseId !== responseId) {
    useSessionStore.setState({ voicePlaybackResponseId: responseId });
  }
}

function syncVoicePlaybackFromCapture(): void {
  const snapshot = useSessionStore.getState();
  if (snapshot.mode !== "voice") {
    return;
  }

  if (!voicePlaybackHoldActive()) {
    syncVoicePlaybackResponseId(null);
    return;
  }

  const captureId = capture.playbackResponseId() ?? playbackResponseId;
  if (captureId) {
    syncVoicePlaybackResponseId(captureId);
  }
}

function voicePlaybackHoldActive(): boolean {
  const state = useSessionStore.getState();
  if (state.mode !== "voice") {
    return false;
  }

  if (state.ttsTransport === "clientSpeech") {
    return clientSpeechPlayer?.activeResponseId() != null;
  }

  if (state.voicePlaybackResponseId == null) {
    return false;
  }

  return (
    capture.playbackResponseId() === state.voicePlaybackResponseId
    || capture.playbackQueued() > 0
    || !capture.playbackClosed()
  );
}

function composerOutputBusy(): boolean {
  const snapshot = useSessionStore.getState();
  return snapshot.liveResponseId != null || voicePlaybackHoldActive();
}

function syncCapture(): void {
  const state = useSessionStore.getState();
  if (state.connection !== "ready") {
    captureStreamId = null;
    if (!state.preflightReady && state.pendingMode !== "voice") {
      capture.release();
      void transcriptLife?.disconnect();
    }
    publishCaptureLive();
    return;
  }

  if (state.mode === "voice" && state.streamId && connection) {
    if (state.sttTransport === "clientTranscript") {
      ensureSpeechAdapters();
      const life = ensureTranscriptLife();
      if (clientTranscriptHeldForAgent || life.isSuspendedForAgentOutput()) {
        publishCaptureLive();
        return;
      }

      if (state.muted) {
        if (life.isListening()) {
          void life.mute().then(() => {
            clearLiveUserTranscript();
            publishCaptureLive();
          });
        } else {
          publishCaptureLive();
        }
        return;
      }

      if (!life.isListening()) {
        if (life.isBlocked() || !state.attachmentId) {
          publishCaptureLive();
          return;
        }

        if (!transcriptStart) {
          const selected = state.agents.find((item) => item.id === state.selectedAgentId) ?? state.agents[0];
          transcriptStart = life
            .enterVoice({
              attachmentId: state.attachmentId ?? "",
              mode: "voice",
              muted: false,
              language: state.conversationLanguage ?? selected?.language ?? "en"
            })
            .finally(() => {
              transcriptStart = null;
              syncClientTranscriptBlockedFromLifecycle();
              publishCaptureLive();
            });
        }
        return;
      }

      publishCaptureLive();
      return;
    }

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

    if (!capture.isStreaming() || captureStreamId !== state.streamId) {
      const hub = connection;
      const sessionId = state.sessionId;
      const attachmentId = state.attachmentId;
      const startedStreamId = state.streamId;
      captureStreamId = startedStreamId;
      void capture.start({
        sendAudio: async (frame) => {
          if (captureStreamId !== startedStreamId || !hub || !sessionId || !attachmentId) {
            return;
          }

          audioFramesSent += 1;
          await hub.send("SendAudio", {
            protocolVersion: 1,
            sessionId,
            attachmentId,
            streamId: startedStreamId,
            frameSequence: frame.frameSequence,
            sampleOffset: frame.sampleOffset,
            data: frame.data
          });
        },
        speechStarted: async (utteranceId, sampleOffset, activityScore) => {
          if (captureStreamId !== startedStreamId || !hub || !sessionId || !attachmentId) {
            return;
          }

          duckLocally();
          commandSequence += 1;
          await invoke("SpeechStarted", "user.speech.started", {
            streamId: startedStreamId,
            utteranceId,
            sampleOffset,
            activityScore
          }, commandSequence);
        },
        speechEnded: async (utteranceId, sampleOffset, activityScore, _closing) => {
          if (!hub || !sessionId || !attachmentId) {
            return;
          }

          if (captureStreamId !== startedStreamId) {
            return;
          }

          commandSequence += 1;
          await invoke("SpeechEnded", "user.speech.ended", {
            streamId: startedStreamId,
            utteranceId,
            sampleOffset,
            durationMs: 0,
            activityScore
          }, commandSequence);
        }
      }).then(() => publishCaptureLive());
      return;
    }

    publishCaptureLive();
    return;
  }

  if (state.pendingMode === "voice" || state.preflightReady) {
    publishCaptureLive();
    return;
  }

  captureStreamId = null;
  capture.release();
  void transcriptLife?.exitVoice();
  publishCaptureLive();
}

export const realtimeTestHooks =
  import.meta.env.MODE === "test"
    ? {
        setConnection(hub: HubConnection | null) {
          connection = hub;
        },
        handleAudioOutput,
        handleEvent,
        syncCapture,
        attachAfterReconnect,
        attachWithBusyRetry,
        handleHubClosed,
        setClientTranscriptHeldForAgent(held: boolean) {
          clientTranscriptHeldForAgent = held;
          publishVoiceInputHeldForAgentOutput(held);
        },
        interruptPlayback(responseId: string) {
          return interruptPlayback(responseId);
        },
        markOutputStarted(responseId: string) {
          outputGate.markStarted(responseId);
        },
        resetOutput() {
          attachLoop += 1;
          reconnectBudgetStarted = 0;
          pendingUserText = null;
          sendRequest = null;
          pendingStops.clear();
          voiceEpoch = 0;
          stoppedResponses.clear();
          flushing = false;
          playbackInterrupt = Promise.resolve();
          pendingAudio.length = 0;
          clearEarlyAudio();
          outputGate.reset();
          playbackStarted = false;
          playbackFinal = false;
          playbackWorkletComplete = false;
          playbackCompletedSent = false;
          playbackResponseId = null;
          syncVoicePlaybackResponseId(null);
          playbackConsumed = 0;
          capture.setOverflowListener(null);
          capture.setPlaybackListener(null);
          capture.setPlaybackCompleteListener(null);
          useSessionStore.setState({ pendingSendQueue: [] });
        }
      }
    : null;

async function startConnection(
  sessionId: string,
  options?: { syncUrl?: "push" | "replace" | "none" }
): Promise<void> {
  const keepPreparedCapture = capture.isPrepared() && useSessionStore.getState().preflightReady;
  await stopConnection({ keepPreparedCapture });
  commandSequence = 0;
  audioFramesSent = 0;
  audioOutputsReceived = 0;
  const epoch = ++connectionEpoch;
  const ownerCapability = await ensureOwnerCapability();
  connection = new HubConnectionBuilder()
    .withUrl("/hubs/session", {
      skipNegotiation: true,
      transport: HttpTransportType.WebSockets,
      headers: { "X-AgentCore-Owner-Capability": ownerCapability }
    })
    .withHubProtocol(new MessagePackHubProtocol())
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds(retryContext) {
        if (retryContext.elapsedMilliseconds >= RECONNECT_BUDGET_MS) {
          return null;
        }

        const delayMs = RECONNECT_DELAYS_MS[Math.min(retryContext.previousRetryCount, RECONNECT_DELAYS_MS.length - 1)];
        return Math.min(delayMs, RECONNECT_BUDGET_MS - retryContext.elapsedMilliseconds);
      }
    })
    .build();
  connection.on("SessionEvent", handleEvent);
  connection.on("AudioOutput", handleAudioOutput);
  connection.onreconnecting(() => {
    if (epoch !== connectionEpoch) {
      return;
    }

    beginReconnectBudget();
    dropLiveTransport("reconnecting");
  });
  connection.onreconnected(() => {
    if (epoch !== connectionEpoch) {
      return;
    }

    void attachAfterReconnect();
  });
  connection.onclose(() => {
    if (epoch !== connectionEpoch) {
      return;
    }

    handleHubClosed();
  });
  useSessionStore.setState({
    connection: "connecting",
    sessionId,
    attachmentId: null,
    pendingMode: keepPreparedCapture ? useSessionStore.getState().pendingMode : null,
    preflightReady: keepPreparedCapture,
    captureLive: false,
    lastServerSequence: 0,
    error: null,
    errorFatal: false
  });
  if (options?.syncUrl && options.syncUrl !== "none") {
    syncBrowserSessionPath(sessionId, options.syncUrl);
  } else if (!options?.syncUrl) {
    syncBrowserSessionPath(sessionId, "replace");
  }
  beginReconnectBudget();
  try {
    await connection.start();
    await attachWithBusyRetry(null);
  } catch (error) {
    markConnectionFailed(error instanceof Error ? error.message : "Attach failed.", { force: true });
  }
}

async function attachAfterReconnect(): Promise<void> {
  if (reconnectBudgetStarted === 0) {
    beginReconnectBudget();
  }

  dropLiveTransport("reconnecting");
  const attached = await attachWithBusyRetry(useSessionStore.getState().lastServerSequence || null);
  if (!attached && useSessionStore.getState().connection === "reconnecting") {
    markConnectionFailed("Connection lost. Retry to continue.", { force: true });
  }
}

async function stopConnection(options?: { keepPreparedCapture?: boolean }): Promise<void> {
  attachLoop += 1;
  abortPlayback();
  if (!options?.keepPreparedCapture) {
    capture.release();
  }
  connectionEpoch += 1;
  useSessionStore.setState({ captureLive: false });
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

let applyRouteTask: Promise<void> | null = null;
let bootstrapTask: Promise<string> | null = null;

export async function bootstrap(): Promise<string> {
  if (bootstrapTask) {
    return bootstrapTask;
  }

  bootstrapTask = bootstrapInner().finally(() => {
    bootstrapTask = null;
  });
  return bootstrapTask;
}

async function bootstrapInner(): Promise<string> {
  const [agents, health] = await Promise.all([listAgents(), getHealth()]);
  useSessionStore.setState({
    agents,
    selectedAgentId: agents[0]?.id ?? "examiner"
  });
  await refreshCatalog(true);
  await applyRouteFromLocation();
  return health.profile;
}

export function applyRouteFromLocation(): Promise<void> {
  if (applyRouteTask) {
    return applyRouteTask;
  }

  applyRouteTask = applyRouteFromLocationInner().finally(() => {
    applyRouteTask = null;
  });
  return applyRouteTask;
}

async function applyRouteFromLocationInner(): Promise<void> {
  const sessionId = parseSessionIdFromPath(window.location.pathname);
  if (!sessionId) {
    return;
  }

  const snapshot = useSessionStore.getState();
  if (sameSessionId(snapshot.sessionId, sessionId) && isOpenSession(snapshot)) {
    return;
  }

  const result = await openSessionById(sessionId, { syncUrl: false });
  if (result === "failed" && !isReadonlySession(useSessionStore.getState())) {
    syncBrowserSessionPath(null, "replace");
    useSessionStore.setState({ routeNotice: "That conversation is unavailable." });
  }
}

export async function navigateFromBrowserHistory(): Promise<void> {
  const sessionId = parseSessionIdFromPath(window.location.pathname);
  if (!sessionId) {
    await beginNewChat({ syncUrl: false });
    return;
  }

  const snapshot = useSessionStore.getState();
  if (sameSessionId(snapshot.sessionId, sessionId) && isOpenSession(snapshot)) {
    return;
  }

  const result = await openSessionById(sessionId, { syncUrl: false });
  if (result === "failed" && !isReadonlySession(useSessionStore.getState())) {
    syncBrowserSessionPath(null, "replace");
    useSessionStore.setState({ routeNotice: "That conversation is unavailable." });
  }
}

export async function startConversation(): Promise<boolean> {
  if (startRequest) {
    return startRequest;
  }

  startRequest = (async () => {
    try {
      const snapshot = useSessionStore.getState();
      if (isReadonlySession(snapshot)) {
        return false;
      }

      if (snapshot.sessionId && snapshot.connection === "ready") {
        return true;
      }

      const agent = snapshot.agents.find((item) => item.id === snapshot.selectedAgentId) ?? snapshot.agents[0];
      if (!agent) {
        throw new Error("Unable to create a session.");
      }

      const created = await createSession(agent.id, agent.version, "text");
      await startConnection(created.sessionId, { syncUrl: "replace" });
      await refreshCatalog(true);
      return useSessionStore.getState().connection === "ready";
    } catch (error) {
      useSessionStore.setState({
        error: error instanceof Error ? error.message : "Unable to start a conversation.",
        errorFatal: false
      });
      return false;
    } finally {
      startRequest = null;
    }
  })();

  return startRequest;
}

export async function beginNewChat(options?: { syncUrl?: boolean; urlMode?: "push" | "replace" }): Promise<void> {
  disposed = true;
  await stopConnection();
  stopReceipts();
  capture.release();
  releaseAllPendingFiles();
  useSessionStore.setState({
    ...emptySession(),
    ...catalogShell(),
    routeNotice: null
  });
  void refreshCatalog(true);
  disposed = false;
  if (options?.syncUrl !== false) {
    syncBrowserSessionPath(null, options?.urlMode ?? "push");
  }
}

export function clearRouteNotice(): void {
  useSessionStore.setState({ routeNotice: null });
}

export type OpenSessionResult = "ready" | "ended" | "blocked" | "failed" | "paused";

function isOpenSession(snapshot: { connection: string; sessionId: string | null; status: string }): boolean {
  return snapshot.connection === "ready"
    || (snapshot.connection === "idle" && (isReadonlySession(snapshot) || snapshot.status === "paused"));
}

export async function openSessionById(
  sessionId: string,
  options?: { syncUrl?: boolean }
): Promise<OpenSessionResult> {
  const catalogItem = useSessionStore
    .getState()
    .catalogItems.find((row) => sameSessionId(row.sessionId, sessionId));
  if (catalogItem) {
    return openCatalogSession(catalogItem, options);
  }

  try {
    const view = await getSession(sessionId);
    return openCatalogSession(
      {
        sessionId,
        status: view.status,
        archived: false,
        ended: view.status === "ended",
        pauseReason: view.pauseReason ?? null
      },
      options
    );
  } catch {
    return openCatalogSession(
      {
        sessionId,
        status: "paused",
        archived: false,
        ended: false
      },
      options
    );
  }
}

export async function openCatalogSession(
  item: {
    sessionId: string;
    status: string;
    archived: boolean;
    ended: boolean;
    pauseReason?: string | null;
  },
  options?: { syncUrl?: boolean }
): Promise<OpenSessionResult> {
  if (item.archived) {
    syncBrowserSessionPath(null, "replace");
    useSessionStore.setState({
      routeNotice: "That conversation is archived. Unarchive it from the chat list to open it."
    });
    return "blocked";
  }

  if (item.ended || item.status === "ended") {
    return showEndedSession(item.sessionId, options);
  }

  if (item.status === "paused" && requiresExplicitResume(item.pauseReason)) {
    return showPausedSession(item.sessionId, options);
  }

  const snapshot = useSessionStore.getState();
  if (sameSessionId(snapshot.sessionId, item.sessionId) && snapshot.connection === "ready") {
    useSessionStore.setState({ routeNotice: null });
    if (options?.syncUrl !== false) {
      syncBrowserSessionPath(item.sessionId, "push");
    }
    return "ready";
  }

  if (options?.syncUrl !== false) {
    syncBrowserSessionPath(item.sessionId, "push");
  }

  useSessionStore.setState({ routeNotice: null });

  try {
    if (item.status !== "attached" && requiresExplicitResume(item.pauseReason)) {
      await reopenSession(item.sessionId);
    }
    await startConnection(item.sessionId, { syncUrl: "none" });
    await refreshCatalog(true);
    if (useSessionStore.getState().connection === "ready") {
      return "ready";
    }

    const ended = await openEndedIfTerminal(item.sessionId);
    if (ended) {
      return ended;
    }

    syncBrowserSessionPath(null, "replace");
    return "failed";
  } catch (error) {
    const ended = await openEndedIfTerminal(item.sessionId);
    if (ended) {
      return ended;
    }

    useSessionStore.setState({
      error: error instanceof Error ? error.message : "Unable to open the session.",
      errorFatal: false
    });
    syncBrowserSessionPath(null, "replace");
    return "failed";
  }
}

export async function resumePausedSession(): Promise<boolean> {
  const snapshot = useSessionStore.getState();
  if (!snapshot.sessionId || snapshot.status !== "paused") {
    return false;
  }

  const sessionId = snapshot.sessionId;
  try {
    await reopenSession(sessionId);
    await startConnection(sessionId, { syncUrl: "none" });
    const latest = useSessionStore.getState();
    if (latest.connection === "ready") {
      useSessionStore.setState({ pauseReason: null });
    }
    return latest.connection === "ready";
  } catch {
    return false;
  }
}

export async function retryConnection(): Promise<void> {
  const snapshot = useSessionStore.getState();
  if (!snapshot.sessionId) {
    return;
  }

  if (isReadonlySession(snapshot)) {
    await showEndedSession(snapshot.sessionId, { syncUrl: false });
    return;
  }

  await startConnection(snapshot.sessionId);
}

const ENDED_HISTORY_PAGE_SIZE = 50;

function mergeHistoryEntries(existing: HistoryEntry[], incoming: HistoryEntry[]): HistoryEntry[] {
  const byId = new Map<string, HistoryEntry>();
  for (const entry of [...incoming, ...existing]) {
    byId.set(entry.entryId, entry);
  }

  return [...byId.values()].sort((left, right) => left.sequence - right.sequence);
}

async function hydrateActiveHistory(sessionId: string | null, bootstrap: HistoryEntry[]): Promise<void> {
  if (!sessionId || bootstrap.length === 0) {
    return;
  }

  const firstSeq = bootstrap[0]?.sequence ?? 1;
  if (firstSeq <= 1) {
    return;
  }

  let after = 0;
  let prefix: HistoryEntry[] = [];
  for (;;) {
    const page = await listSessionMessages(sessionId, after, ENDED_HISTORY_PAGE_SIZE);
    const chunk = historyFromPayload(page.items);
    prefix = mergeHistoryEntries(prefix, chunk.filter((entry) => entry.sequence < firstSeq));
    const highestFetched = prefix.reduce((max, entry) => Math.max(max, entry.sequence), 0);
    if (!page.hasMore || highestFetched >= firstSeq - 1) {
      break;
    }

    after = page.nextAfter;
  }

  if (prefix.length === 0) {
    return;
  }

  const latest = useSessionStore.getState();
  if (!sameSessionId(latest.sessionId, sessionId)) {
    return;
  }

  useSessionStore.setState({
    entries: mergeHistoryEntries(prefix, latest.entries)
  });
}

async function loadAllSessionHistory(sessionId: string): Promise<HistoryPage["items"]> {
  const items: HistoryPage["items"] = [];
  let after = 0;
  for (;;) {
    const page = await listSessionMessages(sessionId, after, ENDED_HISTORY_PAGE_SIZE);
    items.push(...page.items);
    if (!page.hasMore) {
      return items;
    }

    after = page.nextAfter;
  }
}

async function showPausedSession(
  sessionId: string,
  options?: { syncUrl?: boolean }
): Promise<OpenSessionResult> {
  const snapshot = useSessionStore.getState();
  if (
    sameSessionId(snapshot.sessionId, sessionId)
    && snapshot.status === "paused"
    && snapshot.connection === "idle"
  ) {
    useSessionStore.setState({ routeNotice: null });
    if (options?.syncUrl !== false) {
      syncBrowserSessionPath(sessionId, "push");
    }
    return "paused";
  }

  disposed = true;
  await stopConnection();
  stopReceipts();
  capture.release();
  releaseAllPendingFiles();
  disposed = false;

  if (options?.syncUrl !== false) {
    syncBrowserSessionPath(sessionId, "push");
  }

  const agents = useSessionStore.getState().agents;
  useSessionStore.setState({
    ...emptySession(),
    ...catalogShell(),
    connection: "idle",
    sessionId,
    status: "paused",
    routeNotice: null
  });

  try {
    const view = await getSession(sessionId);
    if (view.status === "ended") {
      return showEndedSession(sessionId, options);
    }

    const historyItems = await loadAllSessionHistory(sessionId);
    const agent = agents.find((row) => row.id === view.agentId && row.version === view.agentVersion)
      ?? agents.find((row) => row.id === view.agentId);
    const latest = useSessionStore.getState();
    if (!sameSessionId(latest.sessionId, sessionId)) {
      return "failed";
    }

    useSessionStore.setState({
      sessionId: view.sessionId,
      agentName: agent?.name ?? "",
      agentRole: agent?.role ?? "",
      voiceAvailable: Boolean(agent?.voiceAvailable),
      status: "paused",
      pauseReason: view.pauseReason ?? null,
      entries: historyFromPayload(historyItems),
      lastServerSequence: view.lastEntrySequence ?? 0,
      error: null,
      errorFatal: false
    });
    await refreshCatalog(true);
    return "paused";
  } catch (error) {
    const latest = useSessionStore.getState();
    if (!sameSessionId(latest.sessionId, sessionId)) {
      return "failed";
    }

    useSessionStore.setState({
      connection: "failed",
      status: "paused",
      error: error instanceof Error ? error.message : "Unable to open the conversation.",
      errorFatal: false
    });
    return "failed";
  }
}

async function showEndedSession(
  sessionId: string,
  options?: { syncUrl?: boolean }
): Promise<OpenSessionResult> {
  const snapshot = useSessionStore.getState();
  if (sameSessionId(snapshot.sessionId, sessionId) && isReadonlySession(snapshot) && snapshot.connection === "idle") {
    useSessionStore.setState({ routeNotice: null });
    if (options?.syncUrl !== false) {
      syncBrowserSessionPath(sessionId, "push");
    }
    return "ended";
  }

  disposed = true;
  await stopConnection();
  stopReceipts();
  capture.release();
  releaseAllPendingFiles();
  disposed = false;

  if (options?.syncUrl !== false) {
    syncBrowserSessionPath(sessionId, "push");
  }

  const agents = useSessionStore.getState().agents;
  useSessionStore.setState({
    ...emptySession(),
    ...catalogShell(),
    connection: "connecting",
    sessionId,
    status: "ended",
    routeNotice: null
  });

  try {
    const view = await getSession(sessionId);
    if (view.status === "paused" && requiresExplicitResume(view.pauseReason)) {
      return showPausedSession(sessionId, options);
    }

    if (view.status !== "ended") {
      if (view.status !== "attached" && requiresExplicitResume(view.pauseReason)) {
        await reopenSession(sessionId);
      }
      await startConnection(sessionId, { syncUrl: "none" });
      await refreshCatalog(true);
      return useSessionStore.getState().connection === "ready" ? "ready" : "failed";
    }

    const historyItems = await loadAllSessionHistory(sessionId);
    const agent = agents.find((row) => row.id === view.agentId && row.version === view.agentVersion)
      ?? agents.find((row) => row.id === view.agentId);
    const latest = useSessionStore.getState();
    if (!sameSessionId(latest.sessionId, sessionId)) {
      return "failed";
    }

    useSessionStore.setState({
      connection: "idle",
      sessionId: view.sessionId,
      agentName: agent?.name ?? "",
      agentRole: agent?.role ?? "",
      voiceAvailable: false,
      status: "ended",
      entries: historyFromPayload(historyItems),
      lastServerSequence: view.lastEntrySequence ?? 0,
      error: null,
      errorFatal: false
    });
    void hydrateBoundAttachments(sessionId);
    await refreshCatalog(true);
    return "ended";
  } catch (error) {
    const latest = useSessionStore.getState();
    if (!sameSessionId(latest.sessionId, sessionId)) {
      return "failed";
    }

    useSessionStore.setState({
      connection: "failed",
      status: "ended",
      error: error instanceof Error ? error.message : "Unable to open the conversation.",
      errorFatal: false
    });
    return "failed";
  }
}

async function openEndedIfTerminal(sessionId: string): Promise<OpenSessionResult | null> {
  try {
    const view = await getSession(sessionId);
    if (view.status !== "ended") {
      return null;
    }
  } catch {
    return null;
  }

  return showEndedSession(sessionId, { syncUrl: false });
}

async function refreshEndedHistory(sessionId: string): Promise<void> {
  try {
    const view = await getSession(sessionId);
    if (view.status !== "ended") {
      return;
    }

    const historyItems = await loadAllSessionHistory(sessionId);
    const latest = useSessionStore.getState();
    if (!sameSessionId(latest.sessionId, sessionId) || latest.status !== "ended") {
      return;
    }

    const sequence = view.lastEntrySequence ?? 0;
    if (sequence < latest.lastServerSequence) {
      void hydrateBoundAttachments(sessionId);
      return;
    }

    const nextEntries = historyFromPayload(historyItems);
    if (sequence === latest.lastServerSequence && nextEntries.length < latest.entries.length) {
      void hydrateBoundAttachments(sessionId);
      return;
    }

    useSessionStore.setState({
      entries: nextEntries,
      lastServerSequence: sequence
    });
    void hydrateBoundAttachments(sessionId);
  } catch {
    // Keep the in-memory transcript if durable history cannot be refreshed.
  }
}

function restoreDraft(text: string, error: string, wire?: WireError | null): void {
  const latest = useSessionStore.getState();
  useSessionStore.setState({
    draft: latest.draft === "" ? text : latest.draft,
    ...sessionFailurePatch(error, { wire, category: "Validation", code: "ValidationError" })
  });
}

function composerCanSend(draft: string, pending: PendingAttachment[], connection: string, liveResponseId: string | null, pendingSendQueue: PendingSendItem[]): boolean {
  if (connection !== "ready") {
    return false;
  }

  if (pendingUserText && (pendingUserText.text.trim().length > 0 || pendingUserText.attachmentIds.length > 0)) {
    return !pending.some((item) => item.status !== "ready");
  }

  const hasText = draft.trim().length > 0;
  const complete = pending.filter((item) => item.status === "ready" && item.attachmentId);
  const blocked = pending.some((item) => item.status !== "ready");
  if (blocked) {
    return false;
  }

  if (hasText || complete.length > 0) {
    return true;
  }

  return liveResponseId == null && !voicePlaybackHoldActive() && pendingSendQueue.length > 0;
}

async function hydrateBoundAttachments(sessionId: string | null): Promise<void> {
  if (!sessionId) {
    return;
  }

  try {
    const records = await listAttachments(sessionId);
    const bound = records.filter((item) => item.state === "bound" && item.entryId);
    const byEntry = new Map<string, HistoryAttachment[]>();
    for (const record of bound) {
      const list = byEntry.get(record.entryId!) ?? [];
      list.push({
        attachmentId: record.attachmentId,
        displayName: record.displayName,
        contentType: record.contentType
      });
      byEntry.set(record.entryId!, list);
    }

    const latest = useSessionStore.getState();
    if (!sameSessionId(latest.sessionId, sessionId)) {
      return;
    }

    useSessionStore.setState({
      entries: latest.entries.map((entry) => {
        const attachments = byEntry.get(entry.entryId);
        return attachments ? { ...entry, attachments } : entry;
      })
    });
  } catch {
    // History still shows text when attachment metadata cannot be loaded.
  }
}

async function syncVoiceStage(): Promise<void> {
  const snapshot = useSessionStore.getState();
  if (snapshot.connection !== "ready" || snapshot.mode !== "voice" || !snapshot.sessionId) {
    return;
  }

  const ids = snapshot.pendingAttachments
    .filter((item) => item.status === "ready" && item.attachmentId)
    .map((item) => item.attachmentId!) ;
  if (ids.length === 0) {
    return;
  }

  try {
    await stageAttachments(snapshot.sessionId, ids);
  } catch (error) {
    useSessionStore.setState({
      error: error instanceof Error ? error.message : "Unable to stage attachments.",
      errorFatal: false
    });
  }
}

export function composerSendEnabled(): boolean {
  const snapshot = useSessionStore.getState();
  if (isReadonlySession(snapshot) || snapshot.status === "paused") {
    return false;
  }

  if (!snapshot.sessionId) {
    const hasAgent = snapshot.selectedAgentId.trim().length > 0 && snapshot.agents.length > 0;
    return hasAgent && snapshot.draft.trim().length > 0 && snapshot.connection === "idle";
  }

  return composerCanSend(
    snapshot.draft,
    snapshot.pendingAttachments,
    snapshot.connection,
    snapshot.liveResponseId,
    snapshot.pendingSendQueue);
}

export function composerStopEnabled(): boolean {
  const snapshot = useSessionStore.getState();
  return (
    !isReadonlySession(snapshot)
    && snapshot.status !== "paused"
    && snapshot.connection === "ready"
    && (snapshot.liveResponseId != null || voicePlaybackHoldActive())
  );
}

export async function queueComposerFiles(fileList: File[]): Promise<void> {
  let snapshot = useSessionStore.getState();
  if (isReadonlySession(snapshot)) {
    return;
  }

  if (!snapshot.sessionId) {
    const started = await startConversation();
    if (!started) {
      return;
    }
    snapshot = useSessionStore.getState();
  }

  if (!snapshot.sessionId || snapshot.connection !== "ready") {
    return;
  }

  const remaining = 10 - snapshot.pendingAttachments.length;
  const selected = fileList.slice(0, Math.max(0, remaining));
  const added: PendingAttachment[] = selected.map((file) => {
    const localId = uuid();
    retainPendingFile(localId, file);
    return {
      localId,
      displayName: file.name,
      contentType: file.type || "application/octet-stream",
      byteSize: file.size,
      status: "uploading",
      progress: 0,
      attachmentId: null,
      error: null
    };
  });
  if (added.length === 0) {
    return;
  }

  useSessionStore.setState({ pendingAttachments: [...snapshot.pendingAttachments, ...added] });
  await Promise.all(added.map((item) => uploadQueued(snapshot.sessionId!, item.localId)));
}

async function uploadQueued(sessionId: string, localId: string): Promise<void> {
  const file = pendingFile(localId);
  if (!file) {
    return;
  }

  patchPending(localId, { status: "uploading", progress: 0, error: null });
  try {
    const uploaded = await uploadAttachment(sessionId, localId, file, (progress) => {
      patchPending(localId, { progress });
    });
    patchPending(localId, {
      status: "ready",
      progress: 100,
      attachmentId: uploaded.attachmentId,
      displayName: uploaded.displayName,
      contentType: uploaded.contentType,
      error: null
    });
    await syncVoiceStage();
  } catch (error) {
    if (error instanceof DOMException && error.name === "AbortError") {
      return;
    }

    patchPending(localId, {
      status: "error",
      error: error instanceof Error ? error.message : "Upload failed."
    });
  }
}

function patchPending(localId: string, patch: Partial<PendingAttachment>): void {
  const latest = useSessionStore.getState();
  useSessionStore.setState({
    pendingAttachments: latest.pendingAttachments.map((item) =>
      item.localId === localId ? { ...item, ...patch } : item)
  });
}

export async function retryComposerFile(localId: string): Promise<void> {
  const sessionId = useSessionStore.getState().sessionId;
  if (!sessionId) {
    return;
  }

  await uploadQueued(sessionId, localId);
}

export async function removeComposerFile(localId: string): Promise<void> {
  const snapshot = useSessionStore.getState();
  const item = snapshot.pendingAttachments.find((pending) => pending.localId === localId);
  if (item?.attachmentId && snapshot.sessionId && item.status !== "ready") {
    await abortPendingAttachment(snapshot.sessionId, item.attachmentId).catch(() => undefined);
  } else if (item?.attachmentId && snapshot.sessionId && item.status === "ready") {
    await abortPendingAttachment(snapshot.sessionId, item.attachmentId).catch(() => undefined);
  }

  releasePendingFile(localId);
  useSessionStore.setState({
    pendingAttachments: snapshot.pendingAttachments.filter((pending) => pending.localId !== localId)
  });
  await syncVoiceStage();
}

function historyHasUserEvent(entries: { sourceEventId: string | null; role: string }[], eventId: string): boolean {
  return entries.some((entry) => entry.role === "user" && entry.sourceEventId === eventId);
}

function upsertHistoryEntry(entries: HistoryEntry[], next: HistoryEntry): HistoryEntry[] {
  const index = entries.findIndex((entry) => entry.entryId === next.entryId);
  if (index === -1) {
    return [...entries, next].sort((left, right) => left.sequence - right.sequence);
  }

  const copy = entries.slice();
  copy[index] = next;
  return copy;
}

function nextOptimisticUserSequence(entries: HistoryEntry[]): number {
  return entries.reduce((max, entry) => Math.max(max, entry.sequence), 0) + 0.5;
}

function appendOptimisticUserEntry(
  eventId: string,
  text: string,
  attachments: HistoryAttachment[],
  mode: "text" | "voice"
): void {
  const latest = useSessionStore.getState();
  if (historyHasUserEvent(latest.entries, eventId)) {
    return;
  }

  useSessionStore.setState({
    pendingAttachments: [],
    entries: upsertHistoryEntry(latest.entries, {
      entryId: eventId,
      sequence: nextOptimisticUserSequence(latest.entries),
      sourceEventId: eventId,
      role: "user",
      text,
      responseId: null,
      status: "sending",
      deliveryMode: mode,
      heardTextEndExclusive: text.length,
      receivedTextEndExclusive: text.length,
      createdAt: new Date().toISOString(),
      attachments: attachments.length > 0 ? attachments : undefined
    })
  });
}

function removeOptimisticUserEntry(eventId: string): void {
  useSessionStore.setState({
    entries: useSessionStore.getState().entries.filter(
      (entry) => !(entry.role === "user" && entry.sourceEventId === eventId && entry.status === "sending")
    )
  });
}

function commitOptimisticUserEntry(eventId: string): void {
  useSessionStore.setState({
    entries: useSessionStore.getState().entries.map((entry) =>
      entry.role === "user" && entry.sourceEventId === eventId && entry.status === "sending"
        ? { ...entry, status: "completed" }
        : entry)
  });
}

function clearRestoredPendingAttachments(restored: PendingAttachment[]): void {
  if (restored.length === 0) {
    return;
  }

  for (const item of restored) {
    releasePendingFile(item.localId);
  }

  const latest = useSessionStore.getState();
  const restoredIds = new Set(restored.map((item) => item.localId));
  if (latest.pendingAttachments.some((item) => restoredIds.has(item.localId))) {
    useSessionStore.setState({
      pendingAttachments: latest.pendingAttachments.filter((item) => !restoredIds.has(item.localId))
    });
  }
}

function queuedTextUnits(queue: PendingSendItem[]): number {
  return queue.reduce((sum, item) => sum + item.text.length, 0);
}

function releasePendingSendAttachments(item: PendingSendItem): void {
  for (const attachment of item.attachments) {
    releasePendingFile(attachment.localId);
  }
}

export async function removeQueuedSend(localId: string): Promise<void> {
  const snapshot = useSessionStore.getState();
  const item = snapshot.pendingSendQueue.find((queued) => queued.localId === localId);
  if (!item) {
    return;
  }

  const sessionId = snapshot.sessionId;
  const attachments = item.attachments;
  useSessionStore.setState({
    pendingSendQueue: snapshot.pendingSendQueue.filter((queued) => queued.localId !== localId)
  });

  for (const attachment of attachments) {
    if (sessionId && attachment.attachmentId) {
      void abortPendingAttachment(sessionId, attachment.attachmentId).catch(() => undefined);
    }

    releasePendingFile(attachment.localId);
  }
}

function enqueueLocalSend(text: string, readyFiles: PendingAttachment[], attachmentIds: string[]): boolean {
  const snapshot = useSessionStore.getState();
  if (snapshot.pendingSendQueue.length >= MAX_PENDING_SEND_ITEMS) {
    useSessionStore.setState({
      ...sessionFailurePatch(`You can queue at most ${MAX_PENDING_SEND_ITEMS} messages while the agent is responding.`, {
        category: "Resource",
        code: "QueueLimit"
      })
    });
    return false;
  }

  const nextUnits = queuedTextUnits(snapshot.pendingSendQueue) + text.length;
  if (nextUnits > MAX_PENDING_SEND_TEXT_UNITS) {
    useSessionStore.setState({
      ...sessionFailurePatch(`Queued messages exceed ${MAX_PENDING_SEND_TEXT_UNITS} characters.`, {
        category: "Resource",
        code: "QueueLimit"
      })
    });
    return false;
  }

  if (text.length > MAX_USER_TEXT_UNITS) {
    useSessionStore.setState({
      ...sessionFailurePatch("Text exceeds 8000 UTF-16 code units.", {
        category: "Validation",
        code: "ValidationError"
      })
    });
    return false;
  }

  const item: PendingSendItem = {
    localId: uuid(),
    eventId: uuid(),
    text,
    attachmentIds,
    attachments: readyFiles.map((file) => ({ ...file }))
  };
  useSessionStore.setState({
    pendingSendQueue: [...snapshot.pendingSendQueue, item],
    draft: "",
    pendingAttachments: [],
    error: snapshot.errorFatal ? snapshot.error : null,
    sessionError: snapshot.errorFatal ? snapshot.sessionError : null
  });
  return true;
}

type DispatchSource = { kind: "composer" } | { kind: "queue"; localId: string };

type OutgoingUserMessage = {
  text: string;
  attachmentIds: string[];
  attachments: PendingAttachment[];
  eventId?: string;
  behavior?: "interrupt";
  source?: DispatchSource;
};

function clearQueueItemDispatchState(localId: string): void {
  useSessionStore.setState((state) => ({
    pendingSendQueue: state.pendingSendQueue.map((item) =>
      item.localId === localId ? { ...item, dispatching: false } : item)
  }));
}

async function dispatchOutgoingUserMessage(message: OutgoingUserMessage): Promise<boolean> {
  if (sendRequest) {
    await sendRequest;
    return false;
  }

  let snapshot = useSessionStore.getState();
  if (isReadonlySession(snapshot) || snapshot.connection !== "ready" || !snapshot.sessionId) {
    return false;
  }

  const readyAttachmentIds = message.attachmentIds;
  const text = message.text;
  if (!text && readyAttachmentIds.length === 0) {
    return false;
  }

  const reusePending =
    pendingUserText !== null
    && pendingUserText.text === text
    && pendingUserText.attachmentIds.join() === readyAttachmentIds.join();
  const eventId = reusePending ? pendingUserText!.eventId : message.eventId ?? uuid();
  const pendingAttachmentSnapshot = message.attachments.slice();
  const queueLocalId = message.source?.kind === "queue" ? message.source.localId : null;
  pendingUserText = {
    eventId,
    text,
    attachmentIds: readyAttachmentIds,
    pendingAttachments: pendingAttachmentSnapshot,
    queueLocalId: queueLocalId ?? undefined
  };
  const refs: HistoryAttachment[] = message.attachments
    .filter((item) => item.attachmentId)
    .map((item) => ({
      attachmentId: item.attachmentId!,
      displayName: item.displayName,
      contentType: item.contentType
    }));
  appendOptimisticUserEntry(eventId, text, refs, snapshot.mode);

  commandSequence += 1;
  const payload: Record<string, unknown> = { text, attachmentIds: readyAttachmentIds };
  if (message.behavior === "interrupt") {
    payload.behavior = "interrupt";
  }

  if (queueLocalId) {
    useSessionStore.setState((state) => ({
      pendingSendQueue: state.pendingSendQueue.map((item) =>
        item.localId === queueLocalId ? { ...item, dispatching: true, error: null } : item)
    }));
  }

  let accepted = false;
  sendRequest = (async () => {
    try {
      const ack = await invoke("SendText", "user.text", payload, commandSequence, null, eventId);
      if (!ack?.accepted) {
        pendingUserText = null;
        removeOptimisticUserEntry(eventId);
        const errorMessage = ack?.error?.message ?? "Message was not accepted.";
        const failure = sessionFailurePatch(errorMessage, { wire: ack?.error, category: "Validation", code: "ValidationError" });
        if (queueLocalId) {
          clearQueueItemDispatchState(queueLocalId);
          useSessionStore.setState((state) => ({
            pendingSendQueue: state.pendingSendQueue.map((item) =>
              item.localId === queueLocalId ? { ...item, error: errorMessage } : item),
            ...(state.errorFatal ? {} : failure)
          }));
        } else {
          restoreDraft(text, errorMessage, ack?.error);
        }

        return;
      }

      pendingUserText = null;
      accepted = true;
      for (const item of pendingAttachmentSnapshot) {
        releasePendingFile(item.localId);
      }

      const latest = useSessionStore.getState();
      useSessionStore.setState({
        ...(latest.errorFatal ? {} : clearSessionFailure())
      });
      if (historyHasUserEvent(latest.entries, eventId)) {
        commitOptimisticUserEntry(eventId);
      }
    } catch (error) {
      const errorMessage = error instanceof Error ? error.message : "Message was not accepted.";
      if (queueLocalId) {
        removeOptimisticUserEntry(eventId);
        clearQueueItemDispatchState(queueLocalId);
        useSessionStore.setState((state) => ({
          pendingSendQueue: state.pendingSendQueue.map((item) =>
            item.localId === queueLocalId ? { ...item, error: errorMessage } : item),
          ...sessionFailurePatch(errorMessage, { category: "Transport", code: "SendFailed" })
        }));
      } else {
        useSessionStore.setState({
          pendingAttachments: pendingUserText?.pendingAttachments ?? pendingAttachmentSnapshot,
          ...sessionFailurePatch(errorMessage, { category: "Transport", code: "SendFailed" })
        });
      }
    }
  })();

  try {
    await sendRequest;
  } finally {
    sendRequest = null;
  }

  return accepted;
}

async function maybeAutoDispatchQueueHead(): Promise<void> {
  const snapshot = useSessionStore.getState();
  if (
    isReadonlySession(snapshot)
    || snapshot.connection !== "ready"
    || snapshot.liveResponseId != null
    || voicePlaybackHoldActive()
    || snapshot.pendingSendQueue.length === 0
    || sendRequest
  ) {
    return;
  }

  const head = snapshot.pendingSendQueue[0];
  const accepted = await dispatchOutgoingUserMessage({
    text: head.text,
    attachmentIds: head.attachmentIds,
    attachments: head.attachments,
    eventId: head.eventId,
    source: { kind: "queue", localId: head.localId }
  });
  if (!accepted) {
    return;
  }

  releasePendingSendAttachments(head);
  useSessionStore.setState((state) => ({
    pendingSendQueue: state.pendingSendQueue.filter((item) => item.localId !== head.localId)
  }));
}

export function composerSteerEnabled(localId: string): boolean {
  const snapshot = useSessionStore.getState();
  const item = snapshot.pendingSendQueue.find((queued) => queued.localId === localId);
  const anyDispatching = snapshot.pendingSendQueue.some((queued) => queued.dispatching);
  return (
    !isReadonlySession(snapshot)
    && snapshot.status !== "paused"
    && snapshot.connection === "ready"
    && snapshot.liveResponseId != null
    && item != null
    && !item.dispatching
    && !anyDispatching
  );
}

export function composerSendLabel(): string {
  return composerOutputBusy() ? "Queue" : "Send";
}

export async function steerQueuedSend(localId: string): Promise<void> {
  const snapshot = useSessionStore.getState();
  const item = snapshot.pendingSendQueue.find((queued) => queued.localId === localId);
  if (!item || !composerSteerEnabled(localId)) {
    return;
  }

  const accepted = await dispatchOutgoingUserMessage({
    text: item.text,
    attachmentIds: item.attachmentIds,
    attachments: item.attachments,
    eventId: item.eventId,
    behavior: "interrupt",
    source: { kind: "queue", localId: item.localId }
  });
  if (!accepted) {
    return;
  }

  releasePendingSendAttachments(item);
  useSessionStore.setState((state) => ({
    pendingSendQueue: state.pendingSendQueue.filter((queued) => queued.localId !== localId)
  }));
}

function reconcilePendingUserText(entries: { sourceEventId: string | null; role: string }[]): void {
  if (!pendingUserText) {
    return;
  }

  const queueLocalId = pendingUserText.queueLocalId;
  if (queueLocalId) {
    const eventId = pendingUserText.eventId;
    const attachments = pendingUserText.pendingAttachments;
    if (historyHasUserEvent(entries, eventId)) {
      const queued = useSessionStore.getState().pendingSendQueue.find((item) => item.localId === queueLocalId);
      pendingUserText = null;
      if (queued) {
        releasePendingSendAttachments(queued);
      }
      clearRestoredPendingAttachments(attachments);
      useSessionStore.setState((state) => ({
        pendingSendQueue: state.pendingSendQueue.filter((item) => item.localId !== queueLocalId)
      }));
      return;
    }

    clearQueueItemDispatchState(queueLocalId);
    pendingUserText = null;
    return;
  }

  if (historyHasUserEvent(entries, pendingUserText.eventId)) {
    const text = pendingUserText.text;
    const restored = pendingUserText.pendingAttachments;
    pendingUserText = null;
    const latest = useSessionStore.getState();
    const patch: { draft?: string; pendingAttachments?: PendingAttachment[] } = {};
    if (latest.draft === text) {
      patch.draft = "";
    }
    clearRestoredPendingAttachments(restored);
    if (Object.keys(patch).length > 0) {
      useSessionStore.setState(patch);
    }
    return;
  }

  if (!sendRequest && useSessionStore.getState().connection === "ready") {
    void sendDraft();
  }
}

export async function sendDraft(): Promise<void> {
  if (sendRequest) {
    return sendRequest;
  }

  let snapshot = useSessionStore.getState();
  if (isReadonlySession(snapshot)) {
    return;
  }

  if (snapshot.draft.length > MAX_USER_TEXT_UNITS) {
    useSessionStore.setState({
      ...sessionFailurePatch("Text exceeds 8000 UTF-16 code units.", {
        category: "Validation",
        code: "ValidationError"
      })
    });
    return;
  }

  if (!snapshot.sessionId) {
    const started = await startConversation();
    if (!started) {
      return;
    }
    snapshot = useSessionStore.getState();
  }

  const draft = snapshot.draft.trim();
  const readyFiles = snapshot.pendingAttachments.filter((item) => item.status === "ready" && item.attachmentId);
  const readyAttachmentIds = readyFiles.map((item) => item.attachmentId!);
  const text = draft || pendingUserText?.text || "";
  if (snapshot.connection !== "ready" || snapshot.pendingAttachments.some((item) => item.status !== "ready")) {
    return;
  }

  if (!text && readyAttachmentIds.length === 0) {
    if (!composerOutputBusy() && snapshot.pendingSendQueue.length > 0) {
      await maybeAutoDispatchQueueHead();
    }

    return;
  }

  if (composerOutputBusy()) {
    if (text.length > 0 || readyAttachmentIds.length > 0) {
      enqueueLocalSend(text, readyFiles, readyAttachmentIds);
    } else if (pendingUserText) {
      await dispatchOutgoingUserMessage({
        text: pendingUserText.text,
        attachmentIds: pendingUserText.attachmentIds,
        attachments: pendingUserText.pendingAttachments
      });
    }

    return;
  }

  if (draft) {
    useSessionStore.setState({ draft: "" });
  }

  await dispatchOutgoingUserMessage({
    text,
    attachmentIds: readyAttachmentIds,
    attachments: snapshot.pendingAttachments.filter((item) => item.status === "ready" && item.attachmentId)
  });
}

export async function cancelRenderedResponse(): Promise<void> {
  const snapshot = useSessionStore.getState();
  if (isReadonlySession(snapshot) || snapshot.connection !== "ready" || snapshot.status === "paused") {
    return;
  }

  const liveTarget = snapshot.liveResponseId;
  let playbackTarget: string | null = null;
  if (snapshot.mode === "voice" && snapshot.ttsTransport === "clientSpeech") {
    await ensureClientSpeechPlayer().cancel(liveTarget ?? clientSpeechPlayer?.activeResponseId() ?? undefined);
  } else if (snapshot.mode === "voice" && voicePlaybackHoldActive()) {
    playbackTarget = capture.playbackResponseId() ?? playbackResponseId ?? snapshot.voicePlaybackResponseId;
  }

  if (playbackTarget) {
    await stopVoicePlayback(playbackTarget);
  }

  if (liveTarget) {
    await cancelServerResponse(liveTarget);
  }
}

async function cancelServerResponse(responseId: string): Promise<void> {
  const existing = pendingStops.get(responseId);
  if (existing) {
    return existing;
  }

  commandSequence += 1;
  const sequence = commandSequence;
  const stopPromise = (async () => {
    try {
      const ack = await invoke("CancelResponse", "agent.response.cancel", {}, sequence, responseId);
      const latest = useSessionStore.getState();
      if (ack?.accepted || latest.liveResponseId !== responseId || ack?.error?.code === "StaleCommand") {
        return;
      }

      if (latest.liveResponseId === responseId) {
        useSessionStore.setState({
          error: ack?.error?.message ?? "Stop was not accepted.",
          sessionError: sessionErrorFromWire(ack?.error, ack?.error?.message ?? "Stop was not accepted."),
          errorFatal: false
        });
      }
    } catch (error) {
      const latest = useSessionStore.getState();
      if (latest.liveResponseId !== responseId) {
        return;
      }

      useSessionStore.setState({
        ...sessionFailurePatch(error instanceof Error ? error.message : "Stop was not accepted.", {
          category: "Session",
          code: "StopFailed"
        })
      });
    } finally {
      pendingStops.delete(responseId);
    }
  })();

  pendingStops.set(responseId, stopPromise);
  return stopPromise;
}

export async function requestVoice(): Promise<void> {
  if (voiceRequest) {
    return voiceRequest;
  }

  const snapshot = useSessionStore.getState();
  if (isReadonlySession(snapshot)) {
    return;
  }

  const selected = snapshot.agents.find((item) => item.id === snapshot.selectedAgentId) ?? snapshot.agents[0];
  const voiceAvailable = snapshot.sessionId ? snapshot.voiceAvailable : Boolean(selected?.voiceAvailable);
  if (!voiceAvailable) {
    return;
  }

  if (snapshot.sessionId && !sessionVoiceEnabled()) {
    return;
  }

  if (snapshot.sessionId && snapshot.connection !== "ready") {
    return;
  }

  if (!snapshot.sessionId && snapshot.connection !== "idle") {
    return;
  }

  if (snapshot.mode === "voice" && !transcriptLife?.isBlocked() && (capture.isPrepared() || transcriptLife?.isListening())) {
    return;
  }

  if (
    snapshot.sessionId
    && snapshot.mode === "voice"
    && snapshot.sttTransport === "clientTranscript"
    && snapshot.connection === "ready"
    && transcriptLife?.isBlocked()
  ) {
    voiceRequest = (async () => {
      useSessionStore.setState(clearSessionFailure());
      ensureSpeechAdapters();
      await ensureTranscriptLife().retryRecognition();
    })();

    try {
      await voiceRequest;
    } finally {
      voiceRequest = null;
    }

    return;
  }

  voiceRequest = (async () => {
    const epoch = ++voiceEpoch;
    useSessionStore.setState({ preflightReady: true, ...clearSessionFailure() });
    ensureSpeechAdapters();
    if (!useSessionStore.getState().sessionId) {
      const started = await startConversation();
      if (!started || epoch !== voiceEpoch) {
        capture.release();
        useSessionStore.setState({ preflightReady: false });
        return;
      }
    }

    try {
      const current = useSessionStore.getState();
      const microphone = current.sttTransport !== "clientTranscript";
      const playback = current.ttsTransport !== "clientSpeech";
      const skipCapture = !microphone && !playback;
      if (!skipCapture && !capture.isPrepared()) {
        await capture.preflight({ microphone, playback });
      }
    } catch (error) {
      if (epoch !== voiceEpoch) {
        return;
      }

      capture.release();
      useSessionStore.setState({
        preflightReady: false,
        ...sessionFailurePatch(microphoneError(error), { category: "Speech", code: "SpeechCaptureFailed" })
      });
      return;
    }
    if (epoch !== voiceEpoch) {
      capture.release();
      useSessionStore.setState({ preflightReady: false });
      return;
    }

    commandSequence += 1;
    try {
      const ack = await invoke("SetMode", "session.mode.set", { mode: "voice" }, commandSequence);
      if (epoch !== voiceEpoch) {
        return;
      }

      if (!ack?.accepted) {
        capture.release();
        useSessionStore.setState({
          preflightReady: false,
          ...sessionFailurePatch(ack?.error?.message ?? "Voice mode was rejected.", {
            wire: ack?.error,
            category: "Speech",
            code: "VoiceUnavailable"
          })
        });
      }
    } catch (error) {
      if (epoch !== voiceEpoch) {
        return;
      }

      capture.release();
      useSessionStore.setState({
        preflightReady: false,
        ...sessionFailurePatch(error instanceof Error ? error.message : "Voice mode failed.", {
          category: "Speech",
          code: "VoiceUnavailable"
        })
      });
    }
  })();

  try {
    await voiceRequest;
  } finally {
    voiceRequest = null;
  }
}

export async function cancelVoice(): Promise<void> {
  voiceEpoch += 1;
  commandSequence += 1;
  try {
    const ack = await invoke("SetMode", "session.mode.set", { mode: "text" }, commandSequence);
    if (!ack?.accepted) {
      capture.release();
      void releaseClientSpeech();
      useSessionStore.setState({
        preflightReady: false,
        muted: false,
        ...sessionFailurePatch(ack?.error?.message ?? "Unable to cancel voice.", {
          wire: ack?.error,
          category: "Speech",
          code: "VoiceCancelFailed"
        })
      });
      return;
    }
  } catch (error) {
    capture.release();
    void releaseClientSpeech();
    useSessionStore.setState({
      preflightReady: false,
      muted: false,
      ...sessionFailurePatch(error instanceof Error ? error.message : "Unable to cancel voice.", {
        category: "Speech",
        code: "VoiceCancelFailed"
      })
    });
    return;
  }

  capture.release();
  void releaseClientSpeech();
  useSessionStore.setState({ preflightReady: false, muted: false, ...clearSessionFailure() });
}

export async function setMuted(muted: boolean): Promise<void> {
  const clientTranscript = useSessionStore.getState().sttTransport === "clientTranscript";
  if (muted) {
    if (clientTranscript) {
      await ensureTranscriptLife().mute();
    } else {
      await capture.muteInput();
    }
    publishCaptureLive();
  }

  commandSequence += 1;
  try {
    const ack = await invoke("SetMuted", "session.mute", { muted }, commandSequence);
    if (!ack?.accepted) {
      useSessionStore.setState({ error: ack?.error?.message ?? "Mute failed.", sessionError: sessionErrorFromWire(ack?.error, ack?.error?.message ?? "Mute failed."), errorFatal: false });
      if (muted) {
        syncCapture();
      }
      return;
    }
  } catch (error) {
    useSessionStore.setState({
      ...sessionFailurePatch(error instanceof Error ? error.message : "Mute failed.", { category: "Speech", code: "MuteFailed" })
    });
    if (muted) {
      syncCapture();
    }
    return;
  }

  if (!muted) {
    if (clientTranscript) {
      await ensureTranscriptLife().unmute(useSessionStore.getState().attachmentId ?? "");
    }
    syncCapture();
  }
}

export async function hangUp(): Promise<void> {
  const snapshot = useSessionStore.getState();
  if (snapshot.sessionId) {
    commandSequence += 1;
    let ended = false;
    try {
      const ack = await invoke("EndSession", "session.end", { reason: "userEnded" }, commandSequence);
      ended = Boolean(ack?.accepted);
    } catch {
      ended = false;
    }

    if (!ended) {
      try {
        await endSession(snapshot.sessionId);
      } catch (error) {
        const latest = useSessionStore.getState();
        useSessionStore.setState({
          error: error instanceof Error ? error.message : "Unable to end the session.",
          errorFatal: false,
          errorHoldSequence: latest.lastServerSequence
        });
        return;
      }
    }
  }

  const latest = useSessionStore.getState();
  disposed = true;
  await stopConnection();
  stopReceipts();
  capture.release();
  void releaseClientSpeech();
  releaseAllPendingFiles();
  useSessionStore.setState({
    ...emptySession(),
    ...catalogShell(),
    connection: "idle",
    sessionId: latest.sessionId,
    agentName: latest.agentName,
    agentRole: latest.agentRole,
    status: "ended",
    entries: latest.entries,
    lastServerSequence: latest.lastServerSequence,
    routeNotice: null
  });
  void refreshCatalog(true);
  if (latest.sessionId) {
    void refreshEndedHistory(latest.sessionId);
  }
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
      void releaseClientSpeech();
      await stopConnection();
      useSessionStore.setState({
        connection: "reconnecting",
        pendingMode: null,
        preflightReady: false,
        captureLive: false,
        attachmentId: null
      });
    },
    reconnect: async () => {
      const sessionId = useSessionStore.getState().sessionId;
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
    flushing: () => flushing,
    emitClientSpeech: async (evidence: ClientSpeechEvidence) => {
      ensureTranscriptLife().ingest(evidence);
      await speechEvidenceChain;
    },
    clientSpeechListening: () => Boolean(transcriptLife?.isListening()),
    getUserMediaUsed: () => capture.usedMicrophone(),
    speechDebug: () => {
      const snapshot = useSessionStore.getState();
      return {
        sttTransport: snapshot.sttTransport,
        ttsTransport: snapshot.ttsTransport,
        mode: snapshot.mode,
        attachmentId: snapshot.attachmentId,
        error: snapshot.error,
        evidenceAttempts: speechEvidenceAttempts,
        userTexts: snapshot.entries.filter((entry) => entry.role === "user").map((entry) => entry.text)
      };
    },
    holdFakeSpeechOutput: () => {
      ensureSpeechAdapters();
      injectedSynthesizer?.armHold();
    },
    releaseFakeSpeechOutput: () => {
      injectedSynthesizer?.releaseHold();
    },
    spokenClientSpeech: () => injectedSynthesizer?.spoken.map((item) => item.text) ?? [],
    clientSpeechActive: () => clientSpeechPlayer?.activeResponseId() ?? null
  };
}
