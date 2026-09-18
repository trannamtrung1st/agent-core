import { speechError, type SpeechErrorCode } from "./errors";
import type {
  ClientSpeechEvidence,
  ClientSpeechRecognizer,
  ClientSpeechRecognizerListener
} from "./clientSpeechRecognizer";
import type {
  ClientSpeechSpeakRequest,
  ClientSpeechSynthesizer,
  ClientSpeechSynthesizerListener,
  SpeechVoice,
  SpeechVoiceHint
} from "./clientSpeechSynthesizer";
import { resolveSpeechVoice } from "./resolveSpeechVoice";

export class FakeSpeechRecognizer implements ClientSpeechRecognizer {
  readonly adapterId = "fake" as const;
  private listener: ClientSpeechRecognizerListener | null = null;
  private running = false;
  readonly evidence: ClientSpeechEvidence[] = [];

  get isRunning(): boolean {
    return this.running;
  }

  async start(listener: ClientSpeechRecognizerListener): Promise<void> {
    this.listener = listener;
    this.running = true;
    this.evidence.length = 0;
  }

  async stop(): Promise<void> {
    if (!this.running) {
      return;
    }

    this.running = false;
    this.emit({ kind: "ended", utteranceId: this.lastUtteranceId(), durationMs: 0, activityScore: 0 });
    this.listener = null;
  }

  async cancel(): Promise<void> {
    this.running = false;
    this.listener = null;
  }

  emit(evidence: ClientSpeechEvidence): void {
    this.evidence.push(evidence);
    this.listener?.onEvidence(evidence);
  }

  fail(code: SpeechErrorCode, utteranceId?: string): void {
    const error = speechError(code);
    this.listener?.onError(error);
    this.emit({ kind: "failed", utteranceId: utteranceId ?? this.lastUtteranceId() });
    this.running = false;
  }

  private lastUtteranceId(): string {
    return this.evidence.at(-1)?.utteranceId ?? "00000000-0000-0000-0000-000000000000";
  }
}

export function createFakeSpeechRecognizer(): FakeSpeechRecognizer {
  return new FakeSpeechRecognizer();
}

export class FakeSpeechSynthesizer implements ClientSpeechSynthesizer {
  readonly adapterId = "fake" as const;
  voices: SpeechVoice[];
  readonly spoken: { text: string; voice: SpeechVoice | null }[] = [];
  cancelled = false;
  private speaking = false;

  constructor(voices: SpeechVoice[] = [
    { voiceURI: "fake-en", name: "Fake English", lang: "en-US", default: true },
    { voiceURI: "fake-fr", name: "Fake French", lang: "fr-FR" }
  ]) {
    this.voices = voices;
  }

  listVoices(): SpeechVoice[] {
    return this.voices;
  }

  resolveVoice(hint?: SpeechVoiceHint): SpeechVoice | null {
    return resolveSpeechVoice(this.voices, hint);
  }

  async speak(request: ClientSpeechSpeakRequest, listener?: ClientSpeechSynthesizerListener): Promise<void> {
    this.cancelled = false;
    this.speaking = true;
    const voice = this.resolveVoice(request.hint);
    this.spoken.push({ text: request.text, voice });
    this.speaking = false;
    listener?.onEnd?.();
  }

  async cancel(): Promise<void> {
    this.cancelled = true;
    this.speaking = false;
  }

  get isSpeaking(): boolean {
    return this.speaking;
  }
}

export function createFakeSpeechSynthesizer(voices?: SpeechVoice[]): FakeSpeechSynthesizer {
  return new FakeSpeechSynthesizer(voices);
}
