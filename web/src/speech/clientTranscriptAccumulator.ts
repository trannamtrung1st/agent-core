import { speechError } from "./errors";
import { recordSpeechObservation } from "./speechObservability";
import type { SessionErrorView } from "../features/chat/sessionError";
import type { ClientSpeechEvidence } from "./clientSpeechRecognizer";

export type AccumulatorGate = {
  attachmentId: string;
  epoch: number;
  mode: "text" | "voice";
  muted: boolean;
};

export type ClientTranscriptAccumulatorOptions = {
  now?: () => number;
  minPartialIntervalMs?: number;
  maxRestarts?: number;
  newUtteranceId?: () => string;
};

const DEFAULT_PARTIAL_INTERVAL_MS = 100;
const DEFAULT_MAX_RESTARTS = 3;

/** Merge post-restart recognition onto a frozen prefix without dropping or duplicating overlap. */
export function mergeRecognitionContinuation(prefix: string, incoming: string): string {
  const left = prefix.trim();
  const right = incoming.trim();
  if (!left) {
    return right;
  }

  if (!right) {
    return left;
  }

  if (right.startsWith(left)) {
    return right;
  }

  if (left.startsWith(right)) {
    return left;
  }

  const maxOverlap = Math.min(left.length, right.length);
  for (let overlap = maxOverlap; overlap > 0; overlap -= 1) {
    if (left.endsWith(right.slice(0, overlap))) {
      return `${left}${right.slice(overlap)}`;
    }
  }

  return `${left} ${right}`;
}

export class ClientTranscriptAccumulator {
  private gate: AccumulatorGate | null = null;
  private utteranceId: string | null = null;
  private stable = "";
  private interim = "";
  private revision = 0;
  private lastPartialAt = Number.NEGATIVE_INFINITY;
  private lastPartialText = "";
  private startedSent = false;
  private applicationFinalSent = false;
  private restarts = 0;
  private readonly now: () => number;
  private readonly minPartialIntervalMs: number;
  private readonly maxRestarts: number;
  private readonly newUtteranceId: () => string;
  private readonly outgoing: ClientSpeechEvidence[] = [];

  constructor(
    private readonly emit: (evidence: ClientSpeechEvidence) => void,
    private readonly onError: (error: SessionErrorView) => void,
    options: ClientTranscriptAccumulatorOptions = {}
  ) {
    this.now = options.now ?? (() => Date.now());
    this.minPartialIntervalMs = options.minPartialIntervalMs ?? DEFAULT_PARTIAL_INTERVAL_MS;
    this.maxRestarts = options.maxRestarts ?? DEFAULT_MAX_RESTARTS;
    this.newUtteranceId = options.newUtteranceId ?? (() => crypto.randomUUID());
  }

  get sent(): readonly ClientSpeechEvidence[] {
    return this.outgoing;
  }

  setGate(gate: AccumulatorGate): void {
    this.gate = gate;
  }

  beginSession(gate: AccumulatorGate): void {
    this.dropPending();
    this.gate = gate;
  }

  startUtterance(utteranceId?: string): void {
    this.utteranceId = utteranceId ?? this.newUtteranceId();
    this.stable = "";
    this.interim = "";
    this.revision = 0;
    this.lastPartialText = "";
    this.startedSent = false;
    this.applicationFinalSent = false;
    this.restarts = 0;
    this.send({ kind: "started", utteranceId: this.utteranceId, activityScore: 0.8 });
    this.startedSent = true;
  }

  ingestInterim(text: string, epoch?: number): void {
    if (!this.matchesEpoch(epoch) || !this.utteranceId || this.applicationFinalSent) {
      return;
    }

    this.interim = text.trim();
    this.maybeSendPartial();
  }

  ingestStableChunk(text: string, epoch?: number): void {
    if (!this.matchesEpoch(epoch) || !this.utteranceId || this.applicationFinalSent) {
      return;
    }

    const piece = text.trim();
    if (!piece) {
      return;
    }

    this.stable = mergeRecognitionContinuation(this.stable, piece);
    this.interim = "";
    this.maybeSendPartial();
  }

  commitApplicationFinal(): void {
    if (!this.utteranceId || this.applicationFinalSent) {
      return;
    }

    const text = this.spokenText();
    if (!text) {
      return;
    }

    this.flushPartial(true);
    this.applicationFinalSent = true;
    this.send({ kind: "final", utteranceId: this.utteranceId, text, confidence: 0.9, activityScore: 0.8 });
  }

  endUtterance(durationMs = 0): void {
    if (!this.utteranceId) {
      return;
    }

    this.commitApplicationFinal();
    this.send({ kind: "ended", utteranceId: this.utteranceId, durationMs, activityScore: 0.2 });
    this.utteranceId = null;
    this.interim = "";
  }

  fail(): void {
    if (!this.utteranceId) {
      return;
    }

    this.send({ kind: "failed", utteranceId: this.utteranceId });
    this.utteranceId = null;
    this.interim = "";
    this.applicationFinalSent = false;
  }

  hasOpenUtterance(): boolean {
    return this.utteranceId != null && this.startedSent && !this.applicationFinalSent;
  }

  unexpectedRestart(): boolean {
    if (!this.utteranceId || this.applicationFinalSent || !this.startedSent) {
      return false;
    }

    this.freezeSpokenPrefix();
    this.restarts += 1;
    if (this.restarts > this.maxRestarts) {
      this.onError(speechError("SpeechRecognitionRestartLimit"));
      this.fail();
      return false;
    }

    return true;
  }

  private matchesEpoch(epoch?: number): boolean {
    return epoch === undefined || this.gate?.epoch === epoch;
  }

  private spokenText(): string {
    return mergeRecognitionContinuation(this.stable, this.interim);
  }

  private freezeSpokenPrefix(): void {
    const prefix = this.spokenText();
    if (!prefix) {
      return;
    }

    this.stable = prefix;
    this.interim = "";
    this.lastPartialText = "";
  }

  private maybeSendPartial(): void {
    const now = this.now();
    if (now - this.lastPartialAt < this.minPartialIntervalMs) {
      return;
    }

    this.flushPartial(false);
  }

  private flushPartial(force: boolean): void {
    const text = this.spokenText();
    if (!this.utteranceId || !text || (!force && text === this.lastPartialText)) {
      return;
    }

    const now = this.now();
    if (!force && now - this.lastPartialAt < this.minPartialIntervalMs) {
      return;
    }

    this.lastPartialAt = now;
    this.lastPartialText = text;
    this.revision += 1;
    this.send({ kind: "partial", utteranceId: this.utteranceId, revision: this.revision, text, activityScore: 0.8 });
  }

  private dropPending(): void {
    this.utteranceId = null;
    this.stable = "";
    this.interim = "";
    this.revision = 0;
    this.lastPartialText = "";
    this.startedSent = false;
    this.applicationFinalSent = false;
    this.restarts = 0;
  }

  private canSend(): boolean {
    return this.gate?.mode === "voice" && this.gate.muted === false;
  }

  private send(evidence: ClientSpeechEvidence): void {
    if (!this.canSend()) {
      return;
    }

    const last = this.outgoing.at(-1);
    if (
      last
      && last.kind === evidence.kind
      && last.utteranceId === evidence.utteranceId
      && last.revision === evidence.revision
      && last.text === evidence.text
    ) {
      return;
    }

    this.outgoing.push(evidence);
    this.emit(evidence);
    if (evidence.kind === "partial") {
      recordSpeechObservation({ name: "speech.partial.count", value: 1 });
    }

    if (evidence.kind === "failed") {
      recordSpeechObservation({ name: "speech.error.code", code: "SpeechRecognitionUnavailable" });
    }
  }
}

export function createClientTranscriptAccumulator(
  emit: (evidence: ClientSpeechEvidence) => void,
  onError: (error: SessionErrorView) => void,
  options?: ClientTranscriptAccumulatorOptions
): ClientTranscriptAccumulator {
  return new ClientTranscriptAccumulator(emit, onError, options);
}
