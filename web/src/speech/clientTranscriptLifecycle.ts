import { ClientTranscriptAccumulator } from "./clientTranscriptAccumulator";
import type { ClientSpeechEvidence } from "./clientSpeechRecognizer";
import type { SessionErrorView } from "../features/chat/sessionError";
import type { SpeechTransportService } from "./speechTransport";

export type TranscriptLifecycleGate = {
  attachmentId: string;
  mode: "text" | "voice";
  muted: boolean;
};

export class ClientTranscriptLifecycle {
  private epoch = 0;
  private readonly accumulator: ClientTranscriptAccumulator;
  private listening = false;
  private gate: TranscriptLifecycleGate | null = null;

  constructor(
    private readonly transport: SpeechTransportService,
    emit: (evidence: ClientSpeechEvidence) => void,
    onError: (error: SessionErrorView) => void
  ) {
    this.accumulator = new ClientTranscriptAccumulator(emit, onError, { minPartialIntervalMs: 100 });
  }

  currentEpoch(): number {
    return this.epoch;
  }

  isListening(): boolean {
    return this.listening;
  }

  async enterVoice(gate: TranscriptLifecycleGate): Promise<void> {
    this.gate = gate;
    this.epoch += 1;
    this.accumulator.beginSession({ ...gate, epoch: this.epoch, mode: "voice", muted: false });
    this.transport.setActiveInputTransport("clientTranscript");
    await this.startListening();
  }

  async mute(): Promise<void> {
    this.accumulator.setGate({
      attachmentId: this.attachment(),
      epoch: this.epoch,
      mode: "voice",
      muted: true
    });
    await this.transport.cancelInput();
    this.listening = false;
  }

  async unmute(attachmentId: string): Promise<void> {
    this.gate = { attachmentId, mode: "voice", muted: false };
    this.epoch += 1;
    this.accumulator.beginSession({ attachmentId, epoch: this.epoch, mode: "voice", muted: false });
    await this.startListening();
  }

  async exitVoice(): Promise<void> {
    await this.transport.cancelInput();
    this.listening = false;
    this.accumulator.beginSession({
      attachmentId: this.attachment(),
      epoch: this.epoch,
      mode: "text",
      muted: false
    });
  }

  async disconnect(): Promise<void> {
    await this.transport.cancelInput();
    this.listening = false;
    this.accumulator.beginSession({
      attachmentId: "",
      epoch: this.epoch + 1,
      mode: "text",
      muted: true
    });
  }

  async reconnect(gate: TranscriptLifecycleGate): Promise<void> {
    this.gate = gate;
    this.epoch += 1;
    this.accumulator.beginSession({ ...gate, epoch: this.epoch, mode: "voice", muted: false });
    await this.startListening();
  }

  ingest(evidence: ClientSpeechEvidence, epoch = this.epoch): void {
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
  }

  private attachment(): string {
    return this.gate?.attachmentId ?? "";
  }

  private async startListening(): Promise<void> {
    if (this.listening) {
      return;
    }

    await this.transport.startInput({
      onEvidence: (evidence) => this.ingest(evidence, this.epoch),
      onError: () => undefined
    });
    this.listening = true;
  }
}
