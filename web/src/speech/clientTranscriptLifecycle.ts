import { speechError } from "./errors";
import { ClientTranscriptAccumulator } from "./clientTranscriptAccumulator";
import type { ClientSpeechEvidence } from "./clientSpeechRecognizer";
import type { SessionErrorView } from "../features/chat/sessionError";
import type { SpeechTransportService } from "./speechTransport";
import { recordSpeechObservation } from "./speechObservability";

export type TranscriptLifecycleGate = {
  attachmentId: string;
  mode: "text" | "voice";
  muted: boolean;
  language?: string;
};

export class ClientTranscriptLifecycle {
  private epoch = 0;
  private readonly accumulator: ClientTranscriptAccumulator;
  private listening = false;
  private blocked = false;
  private suspendedForAgentOutput = false;
  private gate: TranscriptLifecycleGate | null = null;
  private readonly reportError: (error: SessionErrorView) => void;

  constructor(
    private readonly transport: SpeechTransportService,
    emit: (evidence: ClientSpeechEvidence) => void,
    onError: (error: SessionErrorView) => void,
    private readonly onStateChange?: () => void,
    private readonly onLiveTranscriptClear?: () => void
  ) {
    this.reportError = onError;
    this.accumulator = new ClientTranscriptAccumulator(
      emit,
      (error) => {
        recordSpeechObservation({ name: "speech.error.code", code: error.code });
        onError(error);
      },
      { minPartialIntervalMs: 100 }
    );
  }

  currentEpoch(): number {
    return this.epoch;
  }

  isListening(): boolean {
    return this.listening;
  }

  isBlocked(): boolean {
    return this.blocked;
  }

  isSuspendedForAgentOutput(): boolean {
    return this.suspendedForAgentOutput;
  }

  async suspendForAgentOutput(): Promise<void> {
    if (this.suspendedForAgentOutput) {
      return;
    }

    this.suspendedForAgentOutput = true;
    this.listening = false;
    if (this.accumulator.hasOpenUtterance()) {
      this.accumulator.fail();
      this.onLiveTranscriptClear?.();
    }

    await this.transport.cancelInput();
    this.notifyStateChange();
  }

  async resumeAfterAgentOutput(): Promise<void> {
    if (!this.suspendedForAgentOutput) {
      return;
    }

    this.suspendedForAgentOutput = false;
    if (this.gate?.mode !== "voice" || this.gate.muted || this.blocked || !this.gate.attachmentId) {
      this.notifyStateChange();
      return;
    }

    this.epoch += 1;
    this.accumulator.beginSession({
      attachmentId: this.gate.attachmentId,
      epoch: this.epoch,
      mode: "voice",
      muted: false
    });
    await this.startListening();
    this.notifyStateChange();
  }

  async enterVoice(gate: TranscriptLifecycleGate): Promise<void> {
    this.suspendedForAgentOutput = false;
    this.gate = gate;
    this.blocked = false;
    this.epoch += 1;
    this.accumulator.beginSession({ ...gate, epoch: this.epoch, mode: "voice", muted: false });
    this.transport.setActiveInputTransport("clientTranscript");
    await this.startListening();
    this.notifyStateChange();
  }

  async retryRecognition(): Promise<boolean> {
    if (!this.blocked || this.gate?.mode !== "voice" || this.gate.muted || !this.gate.attachmentId) {
      return false;
    }

    this.blocked = false;
    this.epoch += 1;
    this.accumulator.beginSession({
      attachmentId: this.gate.attachmentId,
      epoch: this.epoch,
      mode: "voice",
      muted: false
    });
    await this.startListening();
    this.notifyStateChange();
    return !this.blocked && this.listening;
  }

  async mute(): Promise<void> {
    this.listening = false;
    if (this.accumulator.hasOpenUtterance()) {
      this.accumulator.closeForMute();
    }
    this.gate = this.gate
      ? { ...this.gate, muted: true }
      : { attachmentId: this.attachment(), mode: "voice", muted: true };
    this.accumulator.setGate({
      attachmentId: this.attachment(),
      epoch: this.epoch,
      mode: "voice",
      muted: true
    });
    await this.transport.cancelInput();
    this.notifyStateChange();
  }

  async unmute(attachmentId: string): Promise<void> {
    this.gate = { attachmentId, mode: "voice", muted: false, language: this.gate?.language };
    this.blocked = false;
    this.epoch += 1;
    this.accumulator.beginSession({ attachmentId, epoch: this.epoch, mode: "voice", muted: false });
    await this.startListening();
    this.notifyStateChange();
  }

  async exitVoice(): Promise<void> {
    this.listening = false;
    this.blocked = false;
    this.suspendedForAgentOutput = false;
    if (this.accumulator.hasOpenUtterance()) {
      this.accumulator.fail();
      this.onLiveTranscriptClear?.();
    }
    await this.transport.cancelInput();
    this.accumulator.beginSession({
      attachmentId: this.attachment(),
      epoch: this.epoch,
      mode: "text",
      muted: false
    });
    this.notifyStateChange();
  }

  async disconnect(): Promise<void> {
    this.listening = false;
    this.blocked = false;
    this.suspendedForAgentOutput = false;
    this.onLiveTranscriptClear?.();
    await this.transport.cancelInput();
    this.accumulator.beginSession({
      attachmentId: "",
      epoch: this.epoch + 1,
      mode: "text",
      muted: true
    });
    this.notifyStateChange();
  }

  async reconnect(gate: TranscriptLifecycleGate): Promise<void> {
    this.gate = gate;
    this.blocked = false;
    this.epoch += 1;
    this.accumulator.beginSession({ ...gate, epoch: this.epoch, mode: "voice", muted: false });
    await this.startListening();
    this.notifyStateChange();
  }

  ingest(evidence: ClientSpeechEvidence, epoch = this.epoch): void {
    if (this.suspendedForAgentOutput) {
      return;
    }

    if (evidence.kind === "started") {
      this.accumulator.startUtterance(evidence.utteranceId);
      return;
    }

    if (evidence.kind === "partial") {
      this.accumulator.ingestInterim(evidence.text ?? "", epoch);
      return;
    }

    if (evidence.kind === "final") {
      this.accumulator.ingestStableChunk(evidence.text ?? "", epoch);
      return;
    }

    if (evidence.kind === "ended") {
      this.accumulator.endUtterance(evidence.durationMs);
      return;
    }

    this.accumulator.fail();
    this.onLiveTranscriptClear?.();
  }

  private shouldListen(): boolean {
    return (
      this.listening
      && !this.suspendedForAgentOutput
      && this.gate?.mode === "voice"
      && this.gate.muted === false
    );
  }

  private attachment(): string {
    return this.gate?.attachmentId ?? "";
  }

  private async handleRecognitionEnded(): Promise<void> {
    if (!this.shouldListen()) {
      return;
    }

    if (this.accumulator.hasOpenUtterance() && !this.accumulator.unexpectedRestart()) {
      this.listening = false;
      this.blocked = true;
      await this.transport.cancelInput();
      this.notifyStateChange();
    }
  }

  private notifyStateChange(): void {
    this.onStateChange?.();
  }

  private async startListening(): Promise<void> {
    if (this.listening) {
      return;
    }

    let recognitionErrorReported = false;
    try {
      await this.transport.startInput({
        onEvidence: (evidence) => this.ingest(evidence, this.epoch),
        onError: (error) => {
          recognitionErrorReported = true;
          this.listening = false;
          this.blocked = true;
          this.accumulator.fail();
          this.onLiveTranscriptClear?.();
          this.reportError(error);
          this.notifyStateChange();
        },
        onRecognitionEnded: () => {
          void this.handleRecognitionEnded();
        },
        language: this.gate?.language
      });
      if (!this.blocked) {
        this.listening = true;
      }
    } catch {
      this.listening = false;
      this.blocked = true;
      this.accumulator.fail();
      this.onLiveTranscriptClear?.();
      if (!recognitionErrorReported) {
        this.reportError(speechError("SpeechRecognitionUnavailable"));
      }

      this.notifyStateChange();
    }
  }
}
