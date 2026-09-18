import { speechError, speechErrorFromRecognitionError } from "./errors";
import type {
  ClientSpeechRecognizer,
  ClientSpeechRecognizerListener,
  ClientSpeechRecognizerStartOptions
} from "./clientSpeechRecognizer";

type RecognitionErrorEvent = { error?: string };

type BrowserRecognition = {
  continuous: boolean;
  interimResults: boolean;
  lang: string;
  onstart: ((event: Event) => void) | null;
  onend: ((event: Event) => void) | null;
  onerror: ((event: RecognitionErrorEvent) => void) | null;
  onresult: ((event: {
    resultIndex: number;
    results: ArrayLike<{ isFinal: boolean; 0?: { transcript?: string; confidence?: number } }>;
  }) => void) | null;
  onspeechstart: ((event: Event) => void) | null;
  onspeechend: ((event: Event) => void) | null;
  start: () => void;
  stop: () => void;
  abort: () => void;
};

type RecognitionCtor = new () => BrowserRecognition;

const RESTART_BACKOFF_MS = [0, 250, 500];

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

export function recognitionLanguage(language?: string): string {
  const raw = language?.trim();
  return raw && raw.length > 0 ? raw : "en";
}

export class BrowserSpeechRecognizer implements ClientSpeechRecognizer {
  readonly adapterId = "browser" as const;
  private native: BrowserRecognition | null = null;
  private listener: ClientSpeechRecognizerListener | null = null;
  private language = "en";
  private wantRunning = false;
  private generation = 0;
  private utteranceId = "";
  private utteranceOpen = false;
  private revision = 0;
  private startedAt = 0;
  private consecutiveEnds = 0;
  private restartTimer: ReturnType<typeof setTimeout> | null = null;
  private fatal = false;

  async start(listener: ClientSpeechRecognizerListener, options?: ClientSpeechRecognizerStartOptions): Promise<void> {
    const Ctor = recognitionConstructor();
    if (!Ctor) {
      listener.onError(speechError("SpeechUnsupported"));
      throw speechError("SpeechUnsupported");
    }

    this.wantRunning = false;
    this.clearRestart();
    this.generation += 1;
    this.stopNative();
    this.listener = listener;
    this.language = recognitionLanguage(options?.language);
    this.wantRunning = true;
    this.fatal = false;
    this.consecutiveEnds = 0;
    this.beginNative(Ctor, this.generation);
  }

  async stop(): Promise<void> {
    this.wantRunning = false;
    this.clearRestart();
    this.native?.stop();
    this.native = null;
  }

  async cancel(): Promise<void> {
    this.wantRunning = false;
    this.clearRestart();
    this.generation += 1;
    this.stopNative();
    this.listener = null;
  }

  private beginNative(Ctor: RecognitionCtor, generation: number): void {
    if (!this.wantRunning || this.generation !== generation || this.fatal) {
      return;
    }

    this.stopNative();
    const native = new Ctor();
    native.continuous = true;
    native.interimResults = true;
    native.lang = this.language;
    native.onstart = () => undefined;
    native.onspeechstart = () => {
      if (this.generation !== generation) {
        return;
      }

      this.consecutiveEnds = 0;
      if (!this.utteranceOpen) {
        this.beginUtterance();
      }
    };
    native.onresult = (event) => {
      if (this.generation !== generation) {
        return;
      }

      this.consecutiveEnds = 0;
      const results = event.results;
      for (let index = event.resultIndex; index < results.length; index += 1) {
        const result = results[index];
        const alternative = result?.[0];
        const text = alternative?.transcript?.trim() ?? "";
        if (!text) {
          continue;
        }

        if (!this.utteranceOpen) {
          this.beginUtterance();
        }

        this.revision += 1;
        this.listener?.onEvidence({
          kind: result.isFinal ? "final" : "partial",
          utteranceId: this.utteranceId,
          revision: this.revision,
          text,
          confidence: alternative?.confidence
        });
      }
    };
    native.onspeechend = () => {
      if (this.generation !== generation) {
        return;
      }

      this.endUtterance();
    };
    native.onerror = (event) => {
      if (this.generation !== generation) {
        return;
      }

      const raw = event.error;
      if (raw === "no-speech" || raw === "aborted") {
        return;
      }

      this.fatal = true;
      this.wantRunning = false;
      this.clearRestart();
      const error = speechErrorFromRecognitionError(raw);
      this.listener?.onError(error);
      if (this.utteranceOpen) {
        this.listener?.onEvidence({ kind: "failed", utteranceId: this.utteranceId });
        this.utteranceOpen = false;
      }
    };
    native.onend = () => {
      if (this.generation !== generation) {
        return;
      }

      this.native = null;
      if (!this.wantRunning || this.fatal) {
        return;
      }

      this.listener?.onRecognitionEnded?.();
      if (!this.wantRunning || this.fatal || this.generation !== generation) {
        return;
      }

      this.consecutiveEnds += 1;
      const delay = RESTART_BACKOFF_MS[Math.min(this.consecutiveEnds - 1, RESTART_BACKOFF_MS.length - 1)] ?? 500;
      this.clearRestart();
      this.restartTimer = setTimeout(() => {
        this.restartTimer = null;
        this.beginNative(Ctor, generation);
      }, delay);
    };
    this.native = native;
    native.start();
  }

  private beginUtterance(): void {
    if (this.utteranceOpen) {
      this.endUtterance();
    }

    this.utteranceId = crypto.randomUUID();
    this.revision = 0;
    this.startedAt = performance.now();
    this.utteranceOpen = true;
    this.listener?.onEvidence({ kind: "started", utteranceId: this.utteranceId, activityScore: 0.5 });
  }

  private endUtterance(): void {
    if (!this.utteranceOpen) {
      return;
    }

    const durationMs = Math.max(0, Math.round(performance.now() - this.startedAt));
    this.listener?.onEvidence({
      kind: "ended",
      utteranceId: this.utteranceId,
      durationMs,
      activityScore: 0.2
    });
    this.utteranceOpen = false;
  }

  private stopNative(): void {
    this.clearRestart();
    try {
      this.native?.abort();
    } catch {
      // ignored
    }
    this.native = null;
  }

  private clearRestart(): void {
    if (this.restartTimer != null) {
      clearTimeout(this.restartTimer);
      this.restartTimer = null;
    }
  }
}

export function createBrowserSpeechRecognizer(): BrowserSpeechRecognizer {
  return new BrowserSpeechRecognizer();
}
