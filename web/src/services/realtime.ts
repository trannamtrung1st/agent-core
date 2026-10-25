import { HttpTransportType, HubConnection, HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr";
import { MessagePackHubProtocol } from "@microsoft/signalr-protocol-msgpack";
import { newChatPath, parseSessionIdFromPath, sameSessionId, syncBrowserSessionPath } from '../app/sessionRoute';
import { capture } from "../audio/capture";
import { EARLY_AUDIO_MS, OutputAudioGate, type OutputAudioFrame } from "../audio/outputAdmission";
import { INHERIT_MODEL, modelSelectValue } from "../features/chat/ModelPicker";
import { defaultChatIdentityKey, isNewChatIdentityReady, parseChatIdentityKey, resolveNewChatIdentityPresentation } from "../features/chat/chatIdentity";
import { IMAGE_MODEL_INCOMPATIBLE_MESSAGE, imageModelCompatibility, pendingAttachmentsIncludeImage, queuedSendIncludesImage, resolveModelVision } from "../features/chat/imageModelCompatibility";
import { sessionErrorFromMessage, sessionErrorFromWire, type WireError } from "../features/chat/sessionError";
import { createBrowserSpeechRecognizer } from "../speech/browserSpeechRecognizer";
import { createBrowserSpeechSynthesizer } from "../speech/browserSpeechSynthesizer";
import { createClientSpeechPlayer, type ClientSpeechPlayer } from "../speech/clientSpeechPlayer";
import type { ClientSpeechEvidence } from "../speech/clientSpeechRecognizer";
import { clientRecognitionSupported, clientSynthesisSupported, speechTestSeam } from "../speech/clientSpeechSupport";
import type { ClientSpeechSynthesizer } from "../speech/clientSpeechSynthesizer";
import { ClientTranscriptLifecycle } from "../speech/clientTranscriptLifecycle";
import { createFakeSpeechRecognizer, createFakeSpeechSynthesizer, type FakeSpeechRecognizer, type FakeSpeechSynthesizer } from "../speech/fakeSpeechAdapters";
import { speechTransport } from "../speech/speechTransport";
import { voiceControlEnabled } from "../speech/voiceEnablement";
import { applyServerEvent, emptySession, hasControlSequenceGap, hasTextOffsetGap, isReadonlySession, isSessionModelBusy, modelFieldsFromSelection, useSessionStore, type HistoryAttachment, type HistoryEntry, type PendingSendItem, type ServerEvent } from "../state/sessionStore";
import { createSessionForInstance, endSession, getHealth, getSession, listModels, reopenSession } from "./api";
import { abortPendingAttachment, pendingFile, releaseAllPendingFiles, releasePendingFile, retainPendingFile, uploadAttachment, type PendingAttachment } from "./attachments";
import { catalogShell, refreshCatalog } from "./catalog";
import { hasTask, messageText, snapshotParts, wireParts, type MessagePart, type ResourceReference } from "./messageParts";
import { applyReadyHistoryPaging, beginSessionHistory, loadNewestHistoryPage, loadOlderHistoryPage, reconcileOrphanedLiveConversation } from "./sessionHistory";
import { requiresExplicitResume } from "./sessionPauseSemantics";
import { chatTenantId, retireTenantChatRequests, tenantCanCreateChat, tenantCancelWitness, tenantChatCandidates, tenantInterruptWitness, tenantSelectChatModel, tenantSessionMetadata, tenantTextWitness } from './tenantChat';

let connection: HubConnection | null = null;
let connectionEpoch = 0;
let commandSequence = 0;
let clientSpeechPlayer: ClientSpeechPlayer | null = null;
let transcriptLife: ClientTranscriptLifecycle | null = null;
let clientTranscriptHeldForAgent = false;
let injectedRecognizer: FakeSpeechRecognizer | null = null;
let injectedSynthesizer: FakeSpeechSynthesizer | null = null;
let transcriptStart: Promise<void> | null = null;
let commandDispatchChain: Promise<void> = Promise.resolve();
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
let lastReceiptBlockSignature = "";
let disposed = false;
// Chat and Admin share this SPA. Hide media and render receipts outside Chat, not the live SignalR stream.
let chatSurfaceVisible = true;
let navigationVoiceResetSessionId: string | null = null;
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
let voiceModeRequested = false;
let clientOwnsVoice = false;
let captureAuthorized = false;
type ReconnectCause = "transportReconnecting" | "hubClosed" | "controlSequenceGap" | "textOffsetGap" | "explicit";
let lastReconnectCause: ReconnectCause | null = null;
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
  useSessionStore.setState({ draft, draftParts: null });
}

export function setDraftParts(parts: MessagePart[]): void {
  const snapshot = snapshotParts(parts)!;
  useSessionStore.setState({ draft: messageText(snapshot), draftParts: snapshot });
}

export function addResourceToDraft(reference: ResourceReference, label: string): void {
  const state = useSessionStore.getState();
  if (isReadonlySession(state) || state.status === "paused") return;
  const previous = snapshotParts(state.draftParts) ?? (state.draft ? [{kind:"text" as const,text:state.draft}] : []);
  setDraftParts([...previous, ...(previous.length ? [{kind:"text" as const,text:" "}] : []), {kind:"reference",reference,label}, {kind:"text",text:" "}]);
  requestAnimationFrame(() => document.querySelector<HTMLElement>('[aria-label="Message"]')?.focus());
}

export function selectChatIdentity(identityKey: string): void {
  useSessionStore.setState({ newChatIdentityKey: identityKey, draft: "", draftParts: null });
  void loadChatModels();
}

function newChatVoiceAvailable(snapshot: ReturnType<typeof useSessionStore.getState>): boolean {
  const presentation = resolveNewChatIdentityPresentation(
    snapshot.newChatIdentityKey,
    snapshot.chatAgentInstances
  );
  return Boolean(presentation?.voiceAvailable);
}

function command(
  type: string,
  payload: Record<string, unknown>,
  sequence: number,
  responseId: string | null = null,
  eventId: string = uuid()
) {
  const snapshot = useSessionStore.getState();
  return {
    protocolVersion: 1,
    sessionId: snapshot.sessionId,
    eventId,
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
  responseId: string | null = null,
  eventId?: string
) {
  if (!connection) {
    throw new Error("Not connected.");
  }

  const active = connection; const epoch = connectionEpoch;
  const result = await active.invoke(method, command(type, payload, sequence, responseId, eventId)) as {
    accepted?: boolean;
    error?: WireError;
    result?: Record<string, unknown>;
  };
  if (chatTenantId && (connection !== active || connectionEpoch !== epoch)) return { accepted: false };
  // A late Stop acknowledgement for a superseded response must not reattach the new response.
  if (method === 'CancelResponse' && responseId && useSessionStore.getState().liveResponseId !== responseId
    && ['StaleCommand', 'StaleAttachment', 'stale_revision'].includes(result.error?.code ?? '')) return result;
  if (chatTenantId && method !== 'AttachTenant' && ['StaleCommand', 'StaleAttachment', 'stale_revision'].includes(result.error?.code ?? '')) {
    // A retired attachment is a transport fence, including when the server reports it as
    // Authorization. Reattach without replaying this command; actual access is checked again.
    void recoverFromSequenceGap('explicit');
  } else if (chatTenantId && result.error?.category === 'Authorization'
    && !(method === 'SendText' && ['attachment_unavailable', 'interruption_unavailable'].includes(result.error.code ?? ''))
    && !['receipt_unavailable', 'receipt_out_of_bounds', 'UnknownResponse', 'tenant_runtime_unavailable', 'voice_attachment_in_use', 'voice_lease_unavailable'].includes(result.error.code ?? '')) {
    // A queued receipt can lose to Stop's interrupted-row commit. That refusal (and an
    // unknown response target) says nothing about this human's access to the conversation.
    retireUnavailableTenantChat();
  }
  return result;
}

export function retireUnavailableTenantChat(): void {
  const draft = useSessionStore.getState().draft || pendingUserText?.text || '';
  retirePrivateChat(); syncBrowserSessionPath(null, 'replace');
  useSessionStore.setState({ draft, connection: 'failed', error: 'Your access to this conversation changed. Reload your organization or choose another chat.', errorFatal: false });
}
export function releaseDurableApprovalWait(session: string, response: string, run: string): void {
  const current = useSessionStore.getState();
  if (current.sessionId !== session || current.liveResponseId !== response || current.agentRunId !== run) return;
  clearAwaitingAgentResponseStart(); stopReceipts();
  useSessionStore.setState({ liveResponseId: null, agentRunId: null, outputState: 'idle', activeProgress: null });
  void maybeAutoDispatchQueueHead();
}

type HubCommandAck = { accepted?: boolean; error?: WireError; result?: Record<string, unknown> };

function dispatchHubCommand(run: (sequence: number) => Promise<HubCommandAck | void>, prepare?: () => Promise<void>): Promise<HubCommandAck | void> {
  const result = commandDispatchChain.then(async () => {
    if (!connection) {
      return;
    }

    if (chatTenantId && useSessionStore.getState().connection !== 'ready') return;
    const current = connection; const epoch = connectionEpoch;
    const attachment = useSessionStore.getState().attachmentId;
    if (prepare) await prepare();
    if (!connection || connection !== current) return;
    if (chatTenantId && (connectionEpoch !== epoch || useSessionStore.getState().attachmentId !== attachment
      || useSessionStore.getState().connection !== 'ready')) return;
    commandSequence += 1;
    const sequence = commandSequence;
    return run(sequence);
  });
  commandDispatchChain = result.then(
    () => undefined,
    () => undefined
  );
  return result;
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
    clientSpeechPlayer = createClientSpeechPlayer(transportSynthesizer(), sendClientSpeechAck, (error) => {
      useSessionStore.setState({
        error: error.message,
        sessionError: error,
        errorFatal: false
      });
    });
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
    utteranceId: evidence.utteranceId,
    ...(chatTenantId ? { streamId: snapshot.streamId } : {})
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
  void dispatchHubCommand(async (sequence) => {
    const ack = await invoke("SpeechEvidence", "client.speech.evidence", payload, sequence);
    if (ack?.accepted) {
      return ack;
    }

    useSessionStore.setState({
      error: ack?.error?.message ?? "Speech evidence was not accepted.",
      sessionError: sessionErrorFromWire(ack?.error, ack?.error?.message ?? "Speech evidence was not accepted."),
      errorFatal: false
    });
    return ack;
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
  if (!chatSurfaceVisible) return;
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
  if (!chatSurfaceVisible) {
    clearVoiceInputHeldForAgentOutput();
    return;
  }
  if (!clientTranscriptHeldForAgent) {
    return;
  }

  const state = useSessionStore.getState();
  if (state.sttTransport !== "clientTranscript" || state.mode !== "voice") {
    clearVoiceInputHeldForAgentOutput();
    return;
  }

  if (composerOutputBusy()) {
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
let transportRecovery: Promise<void> | null = null;
let awaitingAgentResponseStart = false;

function setCaptureAuthorized(authorized: boolean): void {
  captureAuthorized = authorized;
  if (useSessionStore.getState().captureAuthorized !== authorized) {
    useSessionStore.setState({ captureAuthorized: authorized });
  }
}

function disarmCapture(cause: ReconnectCause): void {
  lastReconnectCause = cause;
  setCaptureAuthorized(false);
  capture.release();
  void transcriptLife?.exitVoice();
}

function clearAwaitingAgentResponseStart(): void {
  awaitingAgentResponseStart = false;
}

function composerOutputBusy(): boolean {
  const snapshot = useSessionStore.getState();
  return (
    awaitingAgentResponseStart
    || voicePlaybackHoldActive()
    || isSessionModelBusy(snapshot)
  );
}

let pendingUserText: {
  parts?: MessagePart[];
  eventId: string;
  text: string;
  attachmentIds: string[];
  pendingAttachments: PendingAttachment[];
  behavior: "queue" | "interrupt";
  queueLocalId?: string;
  interruption?: { interruptedRunId: string; expectedRunRevision: number };
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
  clearAwaitingAgentResponseStart();
  useSessionStore.setState({
    connection,
    pendingMode: null,
    preflightReady: false,
    captureLive: false,
    attachmentId: null,
    activeProgress: null,
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
    activeProgress: null,
    pendingSendQueue: []
  });
}

function isTransientAttachError(ack: { error?: { code?: string } } | null | undefined): boolean {
  const code = ack?.error?.code;
  return code === "SessionInUse" || code === "SessionBusy";
}

async function attachWithBusyRetry(): Promise<boolean> {
  const loop = attachLoop;
  let delayIndex = 0;
  if (!chatTenantId) return false;
  while (loop === attachLoop && connection) {
    try {
      commandSequence = 0;
      commandDispatchChain = Promise.resolve();
      const ack = await invoke(
        "AttachTenant",
        "session.attach",
        { tenantId: chatTenantId },
        0
      );
      if (loop !== attachLoop) {
        return false;
      }

      if (ack?.accepted) {
        reconnectBudgetStarted = 0;
        return true;
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
  // stop() during explicit recovery also raises onclose. The existing recovery owns it.
  if (transportRecovery) return;
  disarmCapture("hubClosed");
  attachLoop += 1;
  if (disposed) {
    return;
  }

  void recoverFromTransientDisconnect();
}

function recoverFromTransientDisconnect(): Promise<void> {
  if (!connection || !useSessionStore.getState().sessionId) {
    markConnectionFailed("Connection lost. Retry to continue.", { force: true });
    return Promise.resolve();
  }
  return recoverFromSequenceGap("hubClosed");
}

function recoverFromSequenceGap(cause: ReconnectCause): Promise<void> {
  if (transportRecovery) return transportRecovery;
  disarmCapture(cause);
  const snapshot = useSessionStore.getState();
  const active = connection; const epoch = connectionEpoch;
  if (!active || !snapshot.sessionId) return Promise.resolve();

  beginReconnectBudget();
  dropLiveTransport("reconnecting");
  // Install ownership before stop() can synchronously raise the close callback.
  const recovery = Promise.resolve().then(async () => {
    try { await active.stop(); } catch { /* Recovery still attempts a fresh connection. */ }
    if (connection !== active || connectionEpoch !== epoch) return;
    try {
      await active.start();
      if (connection !== active || connectionEpoch !== epoch) return;
      const attached = await attachWithBusyRetry();
      if (!attached && connection === active && connectionEpoch === epoch && useSessionStore.getState().connection === "reconnecting") {
        markConnectionFailed("Connection lost. Retry to continue.", { force: true });
      }
    } catch (error) {
      if (connection === active && connectionEpoch === epoch)
        markConnectionFailed(error instanceof Error ? error.message : "Reconnect failed.", { force: true });
    }
  }).finally(() => { if (transportRecovery === recovery) transportRecovery = null; });
  transportRecovery = recovery;
  return recovery;
}

function handleEvent(raw: ServerEvent): void {
  if (!connection) {
    return;
  }

  const prior = useSessionStore.getState();
  if (raw.type === "session.ready") {
    if (!sameSessionId(prior.sessionId, raw.sessionId)) {
      return;
    }
  } else if (isReadonlySession(prior)) {
    return;
  }
  if (hasControlSequenceGap(prior, raw)) {
    abortPlayback();
    useSessionStore.setState(applyServerEvent(prior, raw));
    void recoverFromSequenceGap("controlSequenceGap");
    return;
  }
  if (prior.connection === "ready" && hasTextOffsetGap(prior, raw)) {
    void recoverFromSequenceGap("textOffsetGap");
    return;
  }

  const next = applyServerEvent(prior, raw);
  if (
    chatSurfaceVisible
    && (prior.ttsTransport === "clientSpeech" || next.ttsTransport === "clientSpeech")
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
    if (next.liveResponseId) {
      lastReceiptOffset = -1;
      lastReceiptResponseId = next.liveResponseId;
      lastReceiptBlockSignature = "";
      startReceipts(next.liveResponseId);
    } else {
      stopReceipts();
    }
    const readyPayload = (raw.payload ?? {}) as Record<string, unknown>;
    const hasOlderHistory = Boolean(readyPayload.hasOlderHistory);
    const historyBeforeRaw = readyPayload.historyBeforeSequence;
    const historyBeforeSequence = historyBeforeRaw == null ? null : Number(historyBeforeRaw);
    applyReadyHistoryPaging(
      hasOlderHistory,
      hasOlderHistory && Number.isFinite(historyBeforeSequence) ? historyBeforeSequence : null
    );
    useSessionStore.setState({
      historyHasOlder: hasOlderHistory && historyBeforeSequence != null && Number.isFinite(historyBeforeSequence),
      historyOlderLoading: false
    });
    const orphanPatch = reconcileOrphanedLiveConversation(
      next.entries,
      next.liveResponseId,
      next.outputState
    );
    if (orphanPatch) {
      useSessionStore.setState(orphanPatch);
    }
  }
  if (raw.type === "agent.response.completed" && String(raw.payload.status ?? "completed") === "completed") {
    void maybeAutoDispatchQueueHead();
  }
  if (chatTenantId && raw.type === 'agent.approval.pending') {
    if (raw.responseId === prior.liveResponseId && raw.payload.runId === prior.agentRunId) { clearAwaitingAgentResponseStart(); stopReceipts(); void maybeAutoDispatchQueueHead(); }
    window.dispatchEvent(new Event('agentcore:approval-updated'));
  }
  if (raw.type === "session.state.changed") {
    const status = String(raw.payload.status ?? "");
    if (status === "paused" || status === "ended") {
      clientOwnsVoice = false;
      setCaptureAuthorized(false);
      clearAwaitingAgentResponseStart();
      void stopAfterTerminalCommands();
      stopReceipts();
      capture.release();
      void releaseClientSpeech();
    }
  }
  if (raw.type === "agent.response.started" && raw.responseId) {
    clearAwaitingAgentResponseStart();
    outputGate.markStarted(raw.responseId);
    flushEarlyAudio(raw.responseId);
    if (useSessionStore.getState().sttTransport === "clientTranscript") {
      void suspendClientTranscriptForAgentOutput();
    }
  }
  if (raw.type === "agent.response.started" && raw.responseId) {
    lastReceiptOffset = 0;
    lastReceiptResponseId = raw.responseId;
    lastReceiptBlockSignature = "";
    startReceipts(raw.responseId);
  }
  if ((raw.type === "agent.response.completed" || raw.type === "agent.response.interrupted") && raw.responseId) {
    // The retiring response can terminate after a new input/Steer was accepted.
    // Its terminal event does not start that replacement response. Keep the
    // composer/queue admission hold until response.started (or explicit recovery).
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

export function bytesOf(data: unknown): Uint8Array {
  if (data instanceof Uint8Array) {
    return data;
  }

  if (data instanceof ArrayBuffer) {
    return new Uint8Array(data);
  }

  if (ArrayBuffer.isView(data)) {
    return new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
  }

  if (typeof data === "string") {
    return bytesFromBase64(data);
  }

  if (data && typeof data === "object" && "data" in (data as { data?: unknown }) && Array.isArray((data as { data: unknown }).data)) {
    return Uint8Array.from((data as { data: number[] }).data);
  }

  if (Array.isArray(data)) {
    return Uint8Array.from(data as number[]);
  }

  return new Uint8Array();
}

function bytesFromBase64(value: string): Uint8Array {
  if (value.length === 0 || value.length % 4 !== 0 || !/^[A-Za-z0-9+/]+={0,2}$/.test(value)) {
    return new Uint8Array();
  }

  const binary = atob(value);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index += 1) {
    bytes[index] = binary.charCodeAt(index);
  }

  return bytes;
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
  if (!chatSurfaceVisible || !responseId || !connection || isReadonlySession(useSessionStore.getState())) {
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

function receiptBlockSignature(responseId: string | null): string {
  return renderedBlockIds(responseId).join("\n");
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

  const latest = chatTenantId && !useSessionStore.getState().liveResponseId ? [...entries].reverse().find(entry => entry.role === 'assistant' && entry.status === 'completed' && entry.responseId) : null;
  void sendReceipt(false, latest?.responseId ?? undefined);
}

function startReceipts(responseId: string): void {
  stopReceipts();
  if (!chatSurfaceVisible) return;
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
  if (!connection || !chatSurfaceVisible) {
    return;
  }

  const snapshot = useSessionStore.getState();
  const id = responseId ?? snapshot.liveResponseId ?? lastReceiptResponseId;
  if (!id) {
    return;
  }

  const offset = renderedTextOffset(id);
  const blockSignature = receiptBlockSignature(id);
  if (
    !finalRender
    && offset === lastReceiptOffset
    && lastReceiptResponseId === id
    && blockSignature === lastReceiptBlockSignature
  ) {
    return;
  }

  try {
    const ack = await dispatchHubCommand((sequence) => invoke("ResponseReceived", "response.received",
      { textEndExclusive: offset, blockIds: chatTenantId ? renderedBlockIds(id).slice(0, 32) : renderedBlockIds(id) }, sequence, id));
    if (ack?.accepted) { lastReceiptOffset = offset; lastReceiptResponseId = id; lastReceiptBlockSignature = blockSignature; }
  } catch { /* A lost acknowledgement grants no receipt credit. Fresh attach can retry rendered text. */ }
}

async function sendPlayback(method: string, type: string, responseId: string, consumed: number, textEndExclusive?: number): Promise<void> {
  try {
    await dispatchHubCommand((sequence) =>
      invoke(
        method,
        type,
        {
          consumedSamples: consumed,
          textEndExclusive: textEndExclusive ?? useSessionStore.getState().entries.find(entry => entry.responseId === responseId)?.speechText?.length ?? renderedTextOffset(responseId)
        },
        sequence,
        responseId
      )
    );
  } catch {
    // Playback feedback is best effort. Transport loss proves no local hearing credit;
    // the server receipt transaction owns durability. Never replay it after reattach.
  }
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

  const live = capture.isPrepared() && !state.muted && capture.isStreaming();
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

function syncCapture(): void {
  // A live text transport must never re-arm media while Admin owns the screen.
  if (!chatSurfaceVisible) return;
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
    if (!state.captureAuthorized) {
      publishCaptureLive();
      return;
    }

    if (state.sttTransport === "clientTranscript") {
      ensureSpeechAdapters();
      const life = ensureTranscriptLife();
      if (captureStreamId !== state.streamId) {
        captureStreamId = state.streamId;
        if (life.isListening()) {
          void life.disconnect().then(syncCapture);
          return;
        }
      }
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
              language: state.speechLocale ?? state.conversationLanguage ?? selected?.language ?? "en"
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
          void dispatchHubCommand((sequence) =>
            invoke("SpeechStarted", "user.speech.started", {
              streamId: startedStreamId,
              utteranceId,
              sampleOffset,
              activityScore
            }, sequence)
          );
        },
        speechEnded: async (utteranceId, sampleOffset, activityScore, _closing) => {
          if (!hub || !sessionId || !attachmentId) {
            return;
          }

          if (captureStreamId !== startedStreamId) {
            return;
          }

          void dispatchHubCommand((sequence) =>
            invoke("SpeechEnded", "user.speech.ended", {
              streamId: startedStreamId,
              utteranceId,
              sampleOffset,
              durationMs: 0,
              activityScore
            }, sequence)
          );
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
        sendPlayback,
        sendSpeechEvidence,
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
          chatSurfaceVisible = true;
          navigationVoiceResetSessionId = null;
          attachLoop += 1;
          reconnectBudgetStarted = 0;
          transportRecovery = null;
          lastReconnectCause = null;
          voiceModeRequested = false;
          clientOwnsVoice = false;
          setCaptureAuthorized(false);
          clearAwaitingAgentResponseStart();
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
          stopReceipts();
          lastReceiptOffset = 0;
          lastReceiptResponseId = null;
          lastReceiptBlockSignature = "";
          commandSequence = 0;
          commandDispatchChain = Promise.resolve();
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
  if (!keepPreparedCapture && !voiceModeRequested) {
    voiceEpoch += 1;
    clientTranscriptHeldForAgent = false;
    void releaseClientSpeech();
  }
  await stopConnection({ keepPreparedCapture });
  commandSequence = 0;
  commandDispatchChain = Promise.resolve();
  audioFramesSent = 0;
  audioOutputsReceived = 0;
  const epoch = ++connectionEpoch;
  if (!chatTenantId) throw new Error("Choose an organization before connecting Chat.");
  const builder = new HubConnectionBuilder().withUrl("/hubs/session", {
    skipNegotiation: true,
    transport: HttpTransportType.WebSockets
  });
  const selected =
    import.meta.env.VITE_AGENTCORE_REALTIME_PROTOCOL === "json"
      ? builder
      : builder.withHubProtocol(new MessagePackHubProtocol());
  connection = selected
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
  connection.on("SessionEvent", (raw: ServerEvent) => { if (epoch === connectionEpoch) handleEvent(raw); });
  connection.on("AudioOutput", handleAudioOutput);
  connection.onreconnecting(() => {
    if (epoch !== connectionEpoch) {
      return;
    }

    disarmCapture("transportReconnecting");
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
  const priorShell = useSessionStore.getState();
  const bootstrapLifecycle = !sameSessionId(priorShell.sessionId, sessionId);
  useSessionStore.setState({
    connection: "connecting",
    sessionId,
    attachmentId: null,
    pendingMode: keepPreparedCapture ? useSessionStore.getState().pendingMode : null,
    preflightReady: keepPreparedCapture,
    captureLive: false,
    lastServerSequence: 0,
    error: null,
    errorFatal: false,
    ...(bootstrapLifecycle
      ? {
          status: "created",
          lifecycleStatus: null,
          pauseReason: null
        }
      : {})
  });
  if (options?.syncUrl && options.syncUrl !== "none") {
    syncBrowserSessionPath(sessionId, options.syncUrl);
  } else if (!options?.syncUrl) {
    syncBrowserSessionPath(sessionId, "replace");
  }
  beginReconnectBudget();
  try {
    const metadata = await tenantSessionMetadata(sessionId);
    await loadChatModels(metadata.agentInstanceId);
    await connection.start();
    await attachWithBusyRetry();
    if (chatSurfaceVisible && navigationVoiceResetSessionId === sessionId
      && useSessionStore.getState().connection === "ready") {
      const resetEpoch = voiceEpoch + 1;
      const cancelled = await cancelVoice();
      if (resetEpoch === voiceEpoch && sameSessionId(sessionId, useSessionStore.getState().sessionId)) {
        if (cancelled) {
          navigationVoiceResetSessionId = null;
        } else {
          await stopConnection();
          dropLiveTransport("failed");
        }
      }
    }
  } catch (error) {
    markConnectionFailed(error instanceof Error ? error.message : "Attach failed.", { force: true });
  }
}

async function attachAfterReconnect(): Promise<void> {
  if (reconnectBudgetStarted === 0) {
    beginReconnectBudget();
  }

  dropLiveTransport("reconnecting");
  const attached = await attachWithBusyRetry();
  if (!attached && useSessionStore.getState().connection === "reconnecting") {
    markConnectionFailed("Connection lost. Retry to continue.", { force: true });
  }
}

async function stopAfterTerminalCommands(): Promise<void> {
  const current = connection;
  const sessionId = useSessionStore.getState().sessionId;
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    // A fast request can terminalize before its SendText/render receipt ack arrives.
    // Drain the already queued commands before closing their transport, with a small bound.
    await Promise.race([commandDispatchChain, new Promise<void>(resolve => { timer = setTimeout(resolve, 1000); })]);
  } finally { clearTimeout(timer); }
  const latest = useSessionStore.getState();
  if (connection === current && latest.sessionId === sessionId && (latest.status === "paused" || latest.status === "ended"))
    await stopConnection();
}

async function stopConnection(options?: { keepPreparedCapture?: boolean }): Promise<void> {
  if (!options?.keepPreparedCapture) {
    disarmCapture("explicit");
  }
  attachLoop += 1;
  abortPlayback();
  if (!options?.keepPreparedCapture) {
    capture.release();
  }
  connectionEpoch += 1;
  transportRecovery = null;
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

export function retirePrivateChat(): void {
  disposed = true; candidateEpoch += 1; if (chatTenantId) retireTenantChatRequests(); beginSessionHistory(); stopReceipts(); void stopConnection();
  pendingUserText = null; awaitingAgentResponseStart = false; committedText.clear(); committedBlocks.clear(); releaseAllPendingFiles();
  useSessionStore.setState({ ...emptySession(), agents: [], chatAgentInstances: [], chatAgentInstancesError: null, chatAgentInstancesLoading: false,
    newChatIdentityKey: '', modelCatalog: [], catalogItems: [], catalogNextCursor: null, catalogHasMore: false, catalogError: null });
}

export async function bootstrap(): Promise<string> {
  if (bootstrapTask) {
    return bootstrapTask;
  }

  bootstrapTask = bootstrapInner().finally(() => {
    bootstrapTask = null;
  });
  return bootstrapTask;
}

let candidateEpoch = 0;
async function loadChatAgentInstances(query = '', more = false): Promise<void> {
  if (!chatTenantId) return;

    const epoch = ++candidateEpoch;
    useSessionStore.setState({ chatAgentInstancesLoading: true, chatAgentInstancesError: null });
    try {
      const page = await tenantChatCandidates(more ? useSessionStore.getState().chatAgentPage + 1 : 0, query);
      if (epoch !== candidateEpoch) return;
      const before = useSessionStore.getState();
      const rows = more ? [...before.chatAgentInstances, ...page.items].filter((item, index, all) => all.findIndex(x => x.instanceId === item.instanceId) === index) : page.items;
      const selected = rows.some(item => `managed:${item.instanceId}` === before.newChatIdentityKey) ? before.newChatIdentityKey
        : !query && !before.newChatIdentityKey ? defaultChatIdentityKey(rows) : '';
      useSessionStore.setState({ chatAgentInstances: rows, chatAgentInstancesLoading: false, chatAgentPage: page.page,
        chatAgentQuery: query, chatAgentHasMore: (page.page + 1) * page.pageSize < page.total, newChatIdentityKey: selected });
      if (!more) await loadChatModels();
    } catch (error) {
      if (epoch === candidateEpoch) useSessionStore.setState({ chatAgentInstancesLoading: false, chatAgentInstances: [],
        newChatIdentityKey: '', chatAgentInstancesError: error instanceof Error ? error.message : 'Unable to load agents.' });
    }
    return;
}

export function reloadChatAgentInstances(): Promise<void> {
  return loadChatAgentInstances(useSessionStore.getState().chatAgentQuery);
}
export function searchChatAgents(query: string): Promise<void> { return loadChatAgentInstances(query); }
export function loadMoreChatAgents(): Promise<void> { return loadChatAgentInstances(useSessionStore.getState().chatAgentQuery, true); }

let modelCatalogEpoch = 0;
async function loadChatModels(instanceId?: string): Promise<void> {
  const epoch = ++modelCatalogEpoch;
  const key = useSessionStore.getState().newChatIdentityKey;
  const session = useSessionStore.getState().sessionId;
  const current = () => epoch === modelCatalogEpoch && (session ? sameSessionId(session, useSessionStore.getState().sessionId)
    : !useSessionStore.getState().sessionId && useSessionStore.getState().newChatIdentityKey === key);
  const identity = parseChatIdentityKey(key);
  useSessionStore.setState({ modelCatalog: [], modelCatalogDefaultOfferingId: null,
    modelCatalogDefaultEffort: null, modelCatalogDefaultSource: "tenantDefault", modelCatalogLoading: true, modelCatalogError: null });
  try {
    const models = await listModels(instanceId ?? identity?.instanceId);
    if (!current()) return;
    useSessionStore.setState({ modelCatalog: models.models, modelCatalogDefaultOfferingId: models.defaultOfferingId,
      modelCatalogDefaultEffort: models.defaultReasoningEffort ?? null, modelCatalogDefaultSource: models.defaultSource ?? "tenantDefault",
      modelCatalogLoading: false });
  } catch (error) {
    if (current())
      useSessionStore.setState({ modelCatalogLoading: false, modelCatalogError: error instanceof Error ? error.message : "Unable to load models. Choose the Agent again to retry." });
  }
}

async function bootstrapInner(): Promise<string> {
  if (!chatTenantId) throw new Error("Choose an organization before opening Chat.");
  const requestedSession = parseSessionIdFromPath(window.location.pathname);
  const initial = useSessionStore.getState();
  if (window.location.pathname === newChatPath() && initial.sessionId) {
    // Chat's root route always means New Chat, including a mount from Admin.
    // A /c/{id} mount must keep the SPA-owned transport and live Run intact.
    useSessionStore.setState({ connection: "connecting" });
    await beginNewChat({ syncUrl: false });
  }
  if (requestedSession && (!sameSessionId(initial.sessionId, requestedSession) || !isOpenSession(initial))) {
    // Resolve a deep link before presenting a sendable default-identity chat.
    useSessionStore.setState({ connection: "connecting" });
  }
  const health = await getHealth();
  useSessionStore.setState({
    agents: [],
    chatAgentInstances: [],
    chatAgentInstancesError: null,
    chatAgentInstancesLoading: true,
    newChatIdentityKey: "",
    selectedAgentId: "",
    modelCatalog: [],
    modelCatalogDefaultOfferingId: null,
    modelCatalogDefaultEffort: null,
    modelCatalogLoading: true
  });
  await loadChatAgentInstances();
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
    if (navigationVoiceResetSessionId === sessionId && useSessionStore.getState().connection === "failed") {
      // This known Session is recoverable via Retry. Keep its /c identity rather
      // than presenting its history under the New Chat route.
      return;
    }
    syncBrowserSessionPath(null, "replace");
    useSessionStore.setState({ routeNotice: "That conversation is unavailable." });
  }
}

export async function suspendLiveSessionForNavigation(): Promise<void> {
  // Popstate, router effects and the Admin button may all request suspension.
  if (!chatSurfaceVisible) return;
  const snapshot = useSessionStore.getState();
  const leaveVoice = snapshot.mode === "voice" || snapshot.pendingMode === "voice" || voiceModeRequested;
  // AppRouter swaps ChatApp for AdminApp but the in-flight AgentRun and live
  // SignalR subscription belong to the SPA, not to the Chat component mount.
  // Disconnecting here loses incremental replies and forces an avoidable attach.
  chatSurfaceVisible = false;
  voiceModeRequested = false;
  clientOwnsVoice = false;
  voiceEpoch += 1;
  disarmCapture("explicit");
  abortPlayback();
  stopReceipts();
  void releaseClientSpeech();
  useSessionStore.setState({
    captureLive: false,
    pendingMode: null,
    preflightReady: false,
    liveUserTranscript: null
  });
  // Voice responses require truthful playback ACKs to finish. Once local media
  // is suspended, leave Voice through the existing mode-change protocol rather
  // than strand a Run or acknowledge audio the owner never heard.
  if (leaveVoice && connection && snapshot.connection === "ready") {
    const epoch = voiceEpoch + 1; // cancelVoice invalidates pending media setup.
    const cancelled = await cancelVoice();
    if (!cancelled && epoch === voiceEpoch
      && sameSessionId(snapshot.sessionId, useSessionStore.getState().sessionId)) {
      // Failed mode change cannot leave an attached Voice Run waiting for media
      // we have released. Detach permits the existing headless completion path;
      // keep the error recoverable and let Chat reopen this same Session.
      navigationVoiceResetSessionId = snapshot.sessionId;
      await stopConnection();
      dropLiveTransport("failed");
    }
  }
}

export function resumeLiveSessionForNavigation(): void {
  chatSurfaceVisible = true;
  const snapshot = useSessionStore.getState();
  if (connection && snapshot.connection === "ready" && snapshot.liveResponseId) {
    // The unmounted Chat component could not acknowledge newly rendered text.
    // ChatApp reports the committed entries again after mount.
    startReceipts(snapshot.liveResponseId);
  }
  void sendReceipt(false);
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

  const tenantAtStart = chatTenantId;
  startRequest = (async () => {
    try {
      // Discovery can make the picker visible before bootstrap finishes applying its route.
      // Finish that route first so it cannot replace the connection a Voice click is creating.
      if (bootstrapTask) await bootstrapTask;
      if (tenantAtStart !== chatTenantId || !chatSurfaceVisible) return false;
      const snapshot = useSessionStore.getState();
      if (snapshot.modelCatalogLoading || snapshot.modelCatalogError) return false;
      if (isReadonlySession(snapshot)) {
        return false;
      }

      if (snapshot.sessionId && snapshot.connection === "ready") {
        return true;
      }

      if (
        !snapshot.sessionId
        && !isNewChatIdentityReady({
          chatAgentInstancesLoading: snapshot.chatAgentInstancesLoading,
          newChatIdentityKey: snapshot.newChatIdentityKey,
          chatAgentInstances: snapshot.chatAgentInstances
        })
      ) {
        return false;
      }

      const identity = parseChatIdentityKey(snapshot.newChatIdentityKey);
      if (!identity) {
        return false;
      }
      const modelChoice = {
        offeringId: snapshot.pendingOfferingId === INHERIT_MODEL ? null : snapshot.pendingOfferingId,
        reasoningEffort: snapshot.pendingReasoningEffort
      };
      const created = await createSessionForInstance(identity.instanceId, modelChoice);
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
  voiceEpoch += 1;
  voiceModeRequested = false;
  clientOwnsVoice = false;
  setCaptureAuthorized(false);
  clientTranscriptHeldForAgent = false;
  beginSessionHistory();
  await stopConnection();
  stopReceipts();
  capture.release();
  void releaseClientSpeech();
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
  // A completed approved effect may have advanced the Instance since the previous Chat.
  if (chatTenantId) await loadChatAgentInstances();
}

export function clearRouteNotice(): void {
  useSessionStore.setState({ routeNotice: null });
}

export async function applySessionModel(key: string, reasoningEffort: string | null): Promise<void> {
  const snapshot = useSessionStore.getState();
  if (isReadonlySession(snapshot) || isSessionModelBusy(snapshot)) return;
  if (!snapshot.sessionId) {
    useSessionStore.setState({ pendingOfferingId: key, pendingReasoningEffort: reasoningEffort });
    return;
  }
  if (snapshot.connection !== "ready" || snapshot.status === "paused" || snapshot.mode !== "text") return;
  const session = snapshot.sessionId;
  useSessionStore.setState({ modelMutationPending: true });
  try {
    const saved = await tenantSelectChatModel(session, key === INHERIT_MODEL ? null : key, reasoningEffort);
    if (!sameSessionId(session, useSessionStore.getState().sessionId)) return;
    useSessionStore.setState({ ...modelFieldsFromSelection(saved.model), modelMutationPending: false, sessionError: null, error: null, errorFatal: false });
  } catch (error) {
    if (!sameSessionId(session, useSessionStore.getState().sessionId)) return;
    // Read current state after an uncertain/refused command. Never replay the change.
    try {
      const saved = await getSession(session);
      if (sameSessionId(session, useSessionStore.getState().sessionId))
        useSessionStore.setState({ ...modelFieldsFromSelection(saved.model) });
    } catch { /* Keep the last known selection visible until an explicit reload succeeds. */ }
    if (sameSessionId(session, useSessionStore.getState().sessionId))
      useSessionStore.setState({ modelMutationPending: false,
        ...sessionFailurePatch(error instanceof Error ? error.message : "The model could not be changed. Reload before trying again.") });
  }
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
        pauseReason: view.pauseReason ?? null,
        lifecycleStatus: view.lifecycleStatus ?? null
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
    lifecycleStatus?: string | null;
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
    await startConnection(item.sessionId, { syncUrl: "none" });
    await refreshCatalog(true);
    if (useSessionStore.getState().connection === "ready") {
      return "ready";
    }

    if (navigationVoiceResetSessionId === item.sessionId) {
      return "failed";
    }

    const ended = await openEndedIfTerminal(item.sessionId);
    if (ended) {
      return ended;
    }

    if (options?.syncUrl !== false) {
      syncBrowserSessionPath(null, "replace");
    }
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
    if (options?.syncUrl !== false) {
      syncBrowserSessionPath(null, "replace");
    }
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
    if (chatTenantId) { useSessionStore.setState({ connection: 'idle', error: null, sessionError: null }); await bootstrap(); }
    return;
  }

  if (isReadonlySession(snapshot)) {
    await showEndedSession(snapshot.sessionId, { syncUrl: false });
    return;
  }

  await startConnection(snapshot.sessionId);
}

export async function loadOlderHistory(): Promise<void> {
  await loadOlderHistoryPage();
}

function pinnedIdentity(
  view: { agentName?: string | null; agentRole?: string | null },
  agent: { name?: string; role?: string } | undefined
): { name: string; role: string } {
  return {
    name: view.agentName?.trim() || agent?.name || "",
    role: view.agentRole?.trim() || agent?.role || ""
  };
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

    const agent = agents.find((row) => row.id === view.agentId && row.version === view.agentVersion)
      ?? agents.find((row) => row.id === view.agentId);
    const identity = pinnedIdentity(view, agent);
    const latest = useSessionStore.getState();
    if (!sameSessionId(latest.sessionId, sessionId)) {
      return "failed";
    }

    useSessionStore.setState({
      sessionId: view.sessionId,
      agentName: identity.name,
      agentRole: identity.role,
      voiceAvailable: Boolean(agent?.voiceAvailable),
      status: "paused",
      lifecycleStatus: view.lifecycleStatus ?? "paused",
      pauseReason: view.pauseReason ?? null,
      lastServerSequence: view.lastEntrySequence ?? 0,
      ...modelFieldsFromSelection(view.model),
      error: null,
      errorFatal: false
    });
    await loadNewestHistoryPage(sessionId, { replaceWindow: true });
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
    lifecycleStatus: "ended",
    routeNotice: null
  });

  try {
    const view = await getSession(sessionId);
    if (view.status === "paused" && requiresExplicitResume(view.pauseReason)) {
      return showPausedSession(sessionId, options);
    }

    if (view.status !== "ended") {
      await startConnection(sessionId, { syncUrl: "none" });
      await refreshCatalog(true);
      return useSessionStore.getState().connection === "ready" ? "ready" : "failed";
    }

    const agent = agents.find((row) => row.id === view.agentId && row.version === view.agentVersion)
      ?? agents.find((row) => row.id === view.agentId);
    const identity = pinnedIdentity(view, agent);
    const latest = useSessionStore.getState();
    if (!sameSessionId(latest.sessionId, sessionId)) {
      return "failed";
    }

    useSessionStore.setState({
      connection: "idle",
      sessionId: view.sessionId,
      agentName: identity.name,
      agentRole: identity.role,
      voiceAvailable: false,
      status: "ended",
      lifecycleStatus: view.lifecycleStatus ?? "ended",
      lastServerSequence: view.lastEntrySequence ?? 0,
      ...modelFieldsFromSelection(view.model),
      error: null,
      errorFatal: false
    });
    await loadNewestHistoryPage(sessionId, { replaceWindow: true });
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
      lifecycleStatus: "ended",
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

    const latest = useSessionStore.getState();
    if (!sameSessionId(latest.sessionId, sessionId) || latest.status !== "ended") {
      return;
    }

    if (view.lifecycleStatus && view.lifecycleStatus !== latest.lifecycleStatus) {
      useSessionStore.setState({ lifecycleStatus: view.lifecycleStatus });
    }

    const sequence = view.lastEntrySequence ?? 0;
    if (sequence < latest.lastServerSequence) {
      return;
    }

    await loadNewestHistoryPage(sessionId, { replaceWindow: false });
    const afterLoad = useSessionStore.getState();
    if (sequence === afterLoad.lastServerSequence && afterLoad.entries.length < latest.entries.length) {
      useSessionStore.setState({ entries: latest.entries });
      return;
    }

    useSessionStore.setState({ lastServerSequence: sequence });
  } catch {
    // Keep the in-memory transcript if durable history cannot be refreshed.
  }
}

function restoreDraft(text: string, error: string, wire?: WireError | null, attachments?: PendingAttachment[], parts?: MessagePart[]): void {
  const latest = useSessionStore.getState();
  const code = wire?.code ?? "ValidationError";
  const category = wire?.category ?? "Validation";
  useSessionStore.setState({
    draft: latest.draft === "" ? text : latest.draft,
    draftParts: latest.draft === "" ? snapshotParts(parts) ?? null : latest.draftParts,
    ...(attachments ? { pendingAttachments: mergePendingAttachments(attachments) } : {}),
    ...sessionFailurePatch(error, { wire, category, code })
  });
}

function mergePendingAttachments(restored: PendingAttachment[]): PendingAttachment[] {
  const current = useSessionStore.getState().pendingAttachments;
  const currentIds = new Set(current.map((item) => item.localId));
  return [...current, ...restored.filter((item) => !currentIds.has(item.localId))];
}

function composerModelValue(snapshot = useSessionStore.getState()): string {
  return modelSelectValue(
    snapshot.sessionOfferingId,
    snapshot.pendingOfferingId,
    snapshot.sessionId != null,
    snapshot.modelCatalogDefaultOfferingId);
}

function composerImageCompatibility(snapshot = useSessionStore.getState()) {
  return imageModelCompatibility({
    models: snapshot.modelCatalog,
    modelValue: composerModelValue(snapshot),
    defaultOfferingId: snapshot.modelCatalogDefaultOfferingId,
    pendingAttachments: snapshot.pendingAttachments,
    pendingSendQueue: snapshot.pendingSendQueue
  });
}

export function composerImageIncompatibilityMessage(): string | null {
  const result = composerImageCompatibility();
  return result.incompatible ? result.message : null;
}

function outgoingImageTurnIncompatible(attachments: readonly PendingAttachment[]): boolean {
  if (!pendingAttachmentsIncludeImage(attachments)) {
    return false;
  }

  const snapshot = useSessionStore.getState();
  const vision = resolveModelVision(
    snapshot.modelCatalog,
    composerModelValue(snapshot),
    snapshot.modelCatalogDefaultOfferingId);
  return vision === false;
}

function composerCanSend(
  draft: string,
  pending: PendingAttachment[],
  connection: string,
  pendingSendQueue: PendingSendItem[]
): boolean {
  if (connection !== "ready") {
    return false;
  }

  if (pendingUserText && (pendingUserText.text.trim().length > 0 || pendingUserText.attachmentIds.length > 0)) {
    return !pending.some((item) => item.status !== "ready");
  }

  const hasText = hasTask(useSessionStore.getState().draftParts, draft);
  const complete = pending.filter((item) => item.status === "ready" && item.attachmentId);
  const blocked = pending.some((item) => item.status !== "ready");
  if (blocked) {
    return false;
  }

  if (hasText || complete.length > 0) {
    if (composerImageCompatibility().incompatible) {
      return false;
    }

    return true;
  }

  if (!composerOutputBusy() && pendingSendQueue.length > 0) {
    if (composerImageCompatibility().incompatible) {
      return false;
    }

    return true;
  }

  return false;
}

export function composerSendEnabled(): boolean {
  if (chatTenantId && !useSessionStore.getState().sessionId && !tenantCanCreateChat()) return false;
  const snapshot = useSessionStore.getState();
  if (snapshot.modelMutationPending || !snapshot.sessionId && (snapshot.modelCatalogLoading || snapshot.modelCatalogError)) return false;
  if (isReadonlySession(snapshot) || snapshot.status === "paused") {
    return false;
  }

  if (!snapshot.sessionId) {
    const ready = isNewChatIdentityReady({
      chatAgentInstancesLoading: snapshot.chatAgentInstancesLoading,
      newChatIdentityKey: snapshot.newChatIdentityKey,
      chatAgentInstances: snapshot.chatAgentInstances
    });
    return ready && hasTask(snapshot.draftParts, snapshot.draft) && snapshot.connection === "idle";
  }

  return composerCanSend(
    snapshot.draft,
    snapshot.pendingAttachments,
    snapshot.connection,
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
      readable: uploaded.readable,
      error: null
    });
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
  mode: "text" | "voice",
  parts?: MessagePart[]
): void {
  const latest = useSessionStore.getState();
  if (historyHasUserEvent(latest.entries, eventId)) {
    return;
  }

  useSessionStore.setState({
    pendingAttachments: latest.pendingAttachments.filter((item) =>
      !attachments.some((sent) => sent.attachmentId === item.attachmentId)),
    entries: upsertHistoryEntry(latest.entries, {
      entryId: eventId,
      sequence: nextOptimisticUserSequence(latest.entries),
      sourceEventId: eventId,
      role: "user",
      text,
      parts: snapshotParts(parts),
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

function enqueueLocalSend(text: string, readyFiles: PendingAttachment[], attachmentIds: string[], parts?: MessagePart[]): boolean {
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

  if (outgoingImageTurnIncompatible(readyFiles)) {
    useSessionStore.setState({
      ...sessionFailurePatch(IMAGE_MODEL_INCOMPATIBLE_MESSAGE, {
        category: "Session",
        code: "ModelCapabilityUnsupported"
      })
    });
    return false;
  }

  const item: PendingSendItem = {
    localId: uuid(),
    eventId: uuid(),
    parts: snapshotParts(parts),
    text,
    attachmentIds,
    attachments: readyFiles.map((file) => ({ ...file }))
  };
  useSessionStore.setState({
    pendingSendQueue: [...snapshot.pendingSendQueue, item],
    draft: "",
    draftParts: null,
    pendingAttachments: [],
    error: snapshot.errorFatal ? snapshot.error : null,
    sessionError: snapshot.errorFatal ? snapshot.sessionError : null
  });
  return true;
}

type DispatchSource = { kind: "composer" } | { kind: "queue"; localId: string };

type OutgoingUserMessage = {
  parts?: MessagePart[];
  text: string;
  attachmentIds: string[];
  attachments: PendingAttachment[];
  eventId?: string;
  behavior?: "queue" | "interrupt";
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
  if (isReadonlySession(snapshot) || snapshot.status === "paused" || snapshot.connection !== "ready" || !snapshot.sessionId) {
    return false;
  }

  const readyAttachmentIds = message.attachmentIds;
  const text = message.text;
  if (!text && readyAttachmentIds.length === 0) {
    return false;
  }

  if (outgoingImageTurnIncompatible(message.attachments)) {
    const errorMessage = IMAGE_MODEL_INCOMPATIBLE_MESSAGE;
    const queueLocalIdEarly = message.source?.kind === "queue" ? message.source.localId : null;
    if (queueLocalIdEarly) {
      useSessionStore.setState((state) => ({
        pendingSendQueue: state.pendingSendQueue.map((item) =>
          item.localId === queueLocalIdEarly ? { ...item, error: errorMessage } : item)
      }));
    } else {
      restoreDraft(text, errorMessage, {
        category: "Session",
        code: "ModelCapabilityUnsupported",
        message: errorMessage,
        fatal: false,
        retryAfterMs: null
      }, message.attachments.slice(), message.parts);
    }

    return false;
  }

  const behavior = message.behavior ?? "queue";
  const reusePending =
    pendingUserText !== null
    && pendingUserText.text === text
    && JSON.stringify(pendingUserText.parts) === JSON.stringify(message.parts)
    && pendingUserText.behavior === behavior
    && pendingUserText.attachmentIds.join() === readyAttachmentIds.join();
  const eventId = reusePending ? pendingUserText!.eventId : message.eventId ?? uuid();
  const interruption = reusePending ? pendingUserText?.interruption : undefined;
  const interruptedRun = interruption?.interruptedRunId ?? snapshot.agentRunId;
  const pendingAttachmentSnapshot = message.attachments.slice();
  const queueLocalId = message.source?.kind === "queue" ? message.source.localId : null;
  pendingUserText = {
    parts: snapshotParts(message.parts),
    eventId,
    text,
    attachmentIds: readyAttachmentIds,
    pendingAttachments: pendingAttachmentSnapshot,
    behavior,
    interruption,
    queueLocalId: queueLocalId ?? undefined
  };
  const refs: HistoryAttachment[] = message.attachments
    .filter((item) => item.attachmentId)
    .map((item) => ({
      attachmentId: item.attachmentId!,
      displayName: item.displayName,
      contentType: item.contentType
    }));
  appendOptimisticUserEntry(eventId, text, refs, snapshot.mode, message.parts);

  let payload: Record<string, unknown> = {};

  if (queueLocalId) {
    useSessionStore.setState((state) => ({
      pendingSendQueue: state.pendingSendQueue.map((item) =>
        item.localId === queueLocalId ? { ...item, dispatching: true, error: null } : item)
    }));
  }

  let accepted = false;
  let dispatched = false;
  awaitingAgentResponseStart = true;
  sendRequest = (async () => {
    try {
      const ack = await dispatchHubCommand((sequence) => {
        dispatched = true;
        return invoke("SendText", "user.text", payload, sequence, null, eventId);
      }
      , async () => {
        let witness: Record<string, unknown>;
        if (behavior === 'interrupt') {
          if (!interruptedRun) throw new Error('The response changed. Check the current conversation before steering.');
          if (interruption) witness = { ...await tenantTextWitness(snapshot.sessionId!), ...interruption };
          else {
            witness = await tenantInterruptWitness(snapshot.sessionId!, interruptedRun);
            if (useSessionStore.getState().agentRunId !== interruptedRun) throw new Error('The response changed. Steer the current response explicitly.');
          }
          if (pendingUserText?.eventId === eventId) pendingUserText.interruption = { interruptedRunId: interruptedRun, expectedRunRevision: Number(witness.expectedRunRevision) };
        } else witness = await tenantTextWitness(snapshot.sessionId!);
        payload = { text, ...(message.parts?.length ? { parts: wireParts(message.parts) } : {}), ...(readyAttachmentIds.length ? { attachmentIds: readyAttachmentIds } : {}), ...witness, ...(behavior === 'interrupt' ? { behavior } : {}) };
      });
      if (chatTenantId && useSessionStore.getState().sessionId !== snapshot.sessionId) return;
      if (!ack?.accepted) {
        clearAwaitingAgentResponseStart();
        pendingUserText = null;
        removeOptimisticUserEntry(eventId);
        const errorMessage = ack?.error?.message ?? "Message was not accepted.";
        const failure = sessionFailurePatch(errorMessage, {
          wire: ack?.error,
          category: ack?.error?.category,
          code: ack?.error?.code
        });
        if (queueLocalId) {
          clearQueueItemDispatchState(queueLocalId);
          useSessionStore.setState((state) => ({
            pendingSendQueue: state.pendingSendQueue.map((item) =>
              item.localId === queueLocalId ? { ...item, eventId: uuid(), error: errorMessage } : item),
            ...(state.errorFatal ? {} : failure)
          }));
        } else {
          restoreDraft(text, errorMessage, ack?.error, pendingAttachmentSnapshot, message.parts);
        }

        return;
      }

      pendingUserText = null;
      accepted = true;
      if (chatTenantId && typeof ack.result?.entryId === 'string') useSessionStore.setState(current => ({ entries: current.entries.map(entry =>
        entry.role === 'user' && entry.sourceEventId === eventId ? { ...entry, entryId: String(ack.result!.entryId) } : entry) }));
      clearRestoredPendingAttachments(pendingAttachmentSnapshot);

      const latest = useSessionStore.getState();
      useSessionStore.setState({
        ...(latest.errorFatal ? {} : clearSessionFailure())
      });
      if (historyHasUserEvent(latest.entries, eventId)) {
        commitOptimisticUserEntry(eventId);
      }
    } catch (error) {
      if (chatTenantId && useSessionStore.getState().sessionId !== snapshot.sessionId) return;
      if (!dispatched && pendingUserText?.eventId === eventId) pendingUserText = null;
      clearAwaitingAgentResponseStart();
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
        if (!dispatched) {
          restoreDraft(text, errorMessage, undefined, pendingAttachmentSnapshot, message.parts);
          return;
        }
        useSessionStore.setState({
          pendingAttachments: mergePendingAttachments(pendingUserText?.pendingAttachments ?? pendingAttachmentSnapshot),
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
    || composerOutputBusy()
    || snapshot.pendingSendQueue.length === 0
    || sendRequest
  ) {
    return;
  }

  const head = snapshot.pendingSendQueue[0];
  const accepted = await dispatchOutgoingUserMessage({
    parts: snapshotParts(head.parts),
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
    && !(queuedSendIncludesImage(item) && outgoingImageTurnIncompatible(item.attachments))
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
    parts: snapshotParts(item.parts),
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
    const parts = pendingUserText.parts;
    const restored = pendingUserText.pendingAttachments;
    pendingUserText = null;
    const latest = useSessionStore.getState();
    const patch: { draft?: string; draftParts?: MessagePart[] | null; pendingAttachments?: PendingAttachment[] } = {};
    if (latest.draft === text && JSON.stringify(wireParts(latest.draftParts ?? [])) === JSON.stringify(wireParts(parts ?? []))) {
      patch.draft = "";
      patch.draftParts = null;
    }
    clearRestoredPendingAttachments(restored);
    if (Object.keys(patch).length > 0) {
      useSessionStore.setState(patch);
    }
    return;
  }

  if (!sendRequest && useSessionStore.getState().connection === "ready") {
    // Retry the uncertain command itself, preserving its identity and steer intent.
    // The user may already be composing another message or uploading another file.
    void dispatchOutgoingUserMessage({
      parts: snapshotParts(pendingUserText.parts),
        text: pendingUserText.text,
      attachmentIds: pendingUserText.attachmentIds,
      attachments: pendingUserText.pendingAttachments,
      eventId: pendingUserText.eventId,
      behavior: pendingUserText.behavior
    });
  }
}

export async function sendDraft(behavior: "queue" | "interrupt" = "queue"): Promise<void> {
  let snapshot = useSessionStore.getState();
  if (isReadonlySession(snapshot) || snapshot.status === "paused") {
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

  const draft = snapshot.draftParts ? snapshot.draft : snapshot.draft.trim();
  const parts = snapshotParts(snapshot.draftParts ?? (!draft ? pendingUserText?.parts : undefined));
  if (parts && !hasTask(parts, draft || pendingUserText?.text || "") && snapshot.pendingAttachments.length === 0) return;
  const readyFiles = snapshot.pendingAttachments.filter((item) => item.status === "ready" && item.attachmentId);
  const readyAttachmentIds = readyFiles.map((item) => item.attachmentId!);
  const text = draft || pendingUserText?.text || "";

  if (sendRequest) {
    // An immediate steer must never silently become a queued send.
    if (behavior === "interrupt") return sendRequest;
    if (
      snapshot.connection === "ready"
      && !snapshot.pendingAttachments.some((item) => item.status !== "ready")
      && (text.length > 0 || readyAttachmentIds.length > 0)
    ) {
      enqueueLocalSend(text, readyFiles, readyAttachmentIds, parts);
    }

    return sendRequest;
  }

  if (snapshot.connection !== "ready" || snapshot.pendingAttachments.some((item) => item.status !== "ready")) {
    return;
  }

  if (!text && readyAttachmentIds.length === 0) {
    if (behavior !== "interrupt" && !composerOutputBusy() && snapshot.pendingSendQueue.length > 0) {
      await maybeAutoDispatchQueueHead();
    }

    return;
  }

  if (composerOutputBusy() && behavior !== "interrupt") {
    if (text.length > 0 || readyAttachmentIds.length > 0) {
      enqueueLocalSend(text, readyFiles, readyAttachmentIds, parts);
    } else if (pendingUserText) {
      await dispatchOutgoingUserMessage({
        parts: snapshotParts(pendingUserText.parts),
        text: pendingUserText.text,
        attachmentIds: pendingUserText.attachmentIds,
        attachments: pendingUserText.pendingAttachments
      });
    }

    return;
  }

  if (draft) {
    useSessionStore.setState({ draft: "", draftParts: null });
  }

  await dispatchOutgoingUserMessage({
    parts,
    text,
    attachmentIds: readyAttachmentIds,
    attachments: snapshot.pendingAttachments.filter((item) => item.status === "ready" && item.attachmentId),
    behavior
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

  const stopPromise = (async () => {
    try {
      let payload: Record<string, unknown> = {};
      const ack = await dispatchHubCommand((dispatchSequence) =>
        invoke("CancelResponse", "agent.response.cancel", payload, dispatchSequence, responseId)
      , async () => {
        const run = useSessionStore.getState().agentRunId; if (!run) throw new Error('Reload this conversation before stopping the response.');
        payload = await tenantCancelWitness(run);
      });
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
  if (!chatSurfaceVisible) return;
  if (voiceRequest) {
    return voiceRequest;
  }

  const snapshot = useSessionStore.getState();
  if (isReadonlySession(snapshot)) {
    return;
  }

  const voiceAvailable = snapshot.sessionId ? snapshot.voiceAvailable : newChatVoiceAvailable(snapshot);
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
    voiceModeRequested = true;
    const epoch = ++voiceEpoch;
    try {
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

      const target = useSessionStore.getState();
      const targetConnection = connection;
      const targetEpoch = connectionEpoch;
      const isCurrent = () => epoch === voiceEpoch && connection === targetConnection && connectionEpoch === targetEpoch
        && useSessionStore.getState().sessionId === target.sessionId && useSessionStore.getState().attachmentId === target.attachmentId;
      // The initial ready snapshot clears preflight. Hold prepared media again until mode applies,
      // including when SetMode ACK arrives before the server's pending-mode event.
      useSessionStore.setState({ preflightReady: true });
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
      if (!isCurrent()) {
        capture.release();
        useSessionStore.setState({ preflightReady: false });
        return;
      }

      try {
        const ack = await dispatchHubCommand(async (sequence) => {
          if (!isCurrent()) return { accepted: false };
          return invoke("SetMode", "session.mode.set", { mode: "voice" }, sequence);
        });
        if (!isCurrent()) {
          return;
        }

        if (ack?.accepted) {
          clientOwnsVoice = true;
          setCaptureAuthorized(true);
          syncCapture();
        } else {
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
    } finally {
      if (epoch === voiceEpoch) {
        voiceModeRequested = false;
      }
    }
  })();

  try {
    await voiceRequest;
  } finally {
    voiceRequest = null;
  }
}

export async function resumeCapture(): Promise<void> {
  const state = useSessionStore.getState();
  if (!chatSurfaceVisible || state.mode !== "voice" || state.connection !== "ready") {
    return;
  }

  const epoch = voiceEpoch;
  setCaptureAuthorized(true);
  ensureSpeechAdapters();
  try {
    const microphone = state.sttTransport !== "clientTranscript";
    const playback = state.ttsTransport !== "clientSpeech";
    if ((microphone || playback) && !capture.isPrepared()) {
      await capture.preflight({ microphone, playback });
    }
  } catch (error) {
    if (epoch !== voiceEpoch || !chatSurfaceVisible) return;
    setCaptureAuthorized(false);
    capture.release();
    useSessionStore.setState({
      preflightReady: false,
      ...sessionFailurePatch(microphoneError(error), { category: "Speech", code: "SpeechCaptureFailed" })
    });
    return;
  }

  const current = useSessionStore.getState();
  if (epoch !== voiceEpoch || !chatSurfaceVisible || current.mode !== "voice"
    || current.sessionId !== state.sessionId || current.attachmentId !== state.attachmentId) {
    setCaptureAuthorized(false);
    capture.release();
    return;
  }

  useSessionStore.setState({ preflightReady: true });
  syncCapture();
}

export async function cancelVoice(): Promise<boolean> {
  const snapshot = useSessionStore.getState();
  voiceModeRequested = false;
  clientOwnsVoice = false;
  setCaptureAuthorized(false);
  const epoch = ++voiceEpoch;
  const isCurrent = () => epoch === voiceEpoch
    && sameSessionId(snapshot.sessionId, useSessionStore.getState().sessionId)
    && snapshot.attachmentId === useSessionStore.getState().attachmentId;
  try {
    const ack = await dispatchHubCommand((sequence) =>
      invoke("SetMode", "session.mode.set", { mode: "text" }, sequence)
    );
    if (!isCurrent()) return false;
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
      return false;
    }
  } catch (error) {
    if (!isCurrent()) return false;
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
    return false;
  }

  capture.release();
  void releaseClientSpeech();
  useSessionStore.setState({ preflightReady: false, muted: false, ...clearSessionFailure() });
  return true;
}

export async function setMuted(muted: boolean): Promise<void> {
  const clientTranscript = useSessionStore.getState().sttTransport === "clientTranscript";
  if (muted) {
    if (clientTranscript) {
      await ensureTranscriptLife().mute();
      await commandDispatchChain;
    } else {
      await capture.muteInput();
    }
    publishCaptureLive();
  }

  try {
    const ack = await dispatchHubCommand((sequence) =>
      invoke("SetMuted", "session.mute", { muted }, sequence)
    );
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
    try { await endSession(snapshot.sessionId); }
    catch (error) {
      const latest = useSessionStore.getState();
      useSessionStore.setState({ error: error instanceof Error ? error.message : 'Unable to end the session.', errorFatal: false, errorHoldSequence: latest.lastServerSequence });
      return;
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
    lifecycleStatus: latest.lifecycleStatus ?? "ended",
    entries: latest.entries,
    lastServerSequence: latest.lastServerSequence,
    historyHasOlder: latest.historyHasOlder,
    historyOlderLoading: false,
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
        attachmentId: null,
        activeProgress: null
      });
    },
    reconnect: async () => {
      const sessionId = useSessionStore.getState().sessionId;
      if (sessionId) {
        await startConnection(sessionId);
      }
    },
    reconnectCause: () => lastReconnectCause,
    voiceOwnership: () => clientOwnsVoice,
    captureAuthorized: () => captureAuthorized,
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
      await commandDispatchChain;
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
    clientSpeechActive: () => clientSpeechPlayer?.activeResponseId() ?? null,
    conversationExecution: () => {
      const snapshot = useSessionStore.getState();
      return {
        executionId: snapshot.agentRunId,
        responseId: snapshot.liveResponseId,
        outputState: snapshot.outputState
      };
    },
    hubConnected: () => connection?.state === HubConnectionState.Connected,
    sessionConnection: () => useSessionStore.getState().connection,
    captureLiveState: () => useSessionStore.getState().captureLive
  };
}
