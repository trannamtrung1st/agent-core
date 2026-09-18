import { speechError, speechErrorFromRecognitionError } from "./errors";
import type { ClientSpeechRecognizer, ClientSpeechRecognizerListener } from "./clientSpeechRecognizer";

type RecognitionErrorEvent = { error?: string };

type BrowserRecognition = {
  continuous: boolean;
  interimResults: boolean;
  lang: string;
  onstart: ((event: Event) => void) | null;
  onend: ((event: Event) => void) | null;
  onerror: ((event: RecognitionErrorEvent) => void) | null;
  onresult: ((event: { resultIndex: number; results: ArrayLike<{ isFinal: boolean; 0?: { transcript?: string; confidence?: number } }> }) => void) | null;
  start: () => void;
  stop: () => void;
  abort: () => void;
};

type RecognitionCtor = new () => BrowserRecognition;

function recognitionConstructor(): RecognitionCtor | null {
  const host = window as Window & {
    SpeechRecognition?: RecognitionCtor;
    webkitSpeechRecognition?: RecognitionCtor;
  };
  return host.SpeechRecognition ?? host.webkitSpeechRecognition ?? null;
}

export function browserSpeechRecognitionSupported(): boolean {
  return recognitionConstructor() !== null;
}

export class BrowserSpeechRecognizer implements ClientSpeechRecognizer {
  readonly adapterId = "browser" as const;
  private native: BrowserRecognition | null = null;
  private utteranceId = "";
  private revision = 0;
  private startedAt = 0;

  async start(listener: ClientSpeechRecognizerListener): Promise<void> {
    const Ctor = recognitionConstructor();
    if (!Ctor) {
      listener.onError(speechError("SpeechUnsupported"));
      throw speechError("SpeechUnsupported");
    }

    this.stopNative();
    this.utteranceId = crypto.randomUUID();
    this.revision = 0;
    this.startedAt = performance.now();
    const native = new Ctor();
    native.continuous = true;
    native.interimResults = true;
    native.lang = "en-US";
    native.onstart = () => {
      listener.onEvidence({ kind: "started", utteranceId: this.utteranceId, activityScore: 0.5 });
    };
    native.onresult = (event) => {
      const result = event.results[event.resultIndex];
      const alternative = result?.[0];
      const text = alternative?.transcript?.trim() ?? "";
      if (!text) {
        return;
      }

      this.revision += 1;
      listener.onEvidence({
        kind: "partial",
        utteranceId: this.utteranceId,
        revision: this.revision,
        text,
        confidence: alternative?.confidence
      });
    };
    native.onerror = (event) => {
      const error = speechErrorFromRecognitionError(event.error);
      listener.onError(error);
      listener.onEvidence({ kind: "failed", utteranceId: this.utteranceId });
    };
    native.onend = () => {
      const durationMs = Math.max(0, Math.round(performance.now() - this.startedAt));
      listener.onEvidence({ kind: "ended", utteranceId: this.utteranceId, durationMs, activityScore: 0.2 });
    };
    this.native = native;
    native.start();
  }

  async stop(): Promise<void> {
    this.native?.stop();
    this.native = null;
  }

  async cancel(): Promise<void> {
    this.native?.abort();
    this.native = null;
  }

  private stopNative(): void {
    try {
      this.native?.abort();
    } catch {
      // ignored
    }
    this.native = null;
  }
}

export function createBrowserSpeechRecognizer(): BrowserSpeechRecognizer {
  return new BrowserSpeechRecognizer();
}
