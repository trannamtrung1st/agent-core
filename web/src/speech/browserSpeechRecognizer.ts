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

const UTTERANCE_RESTART_BACKOFF_MS = [0, 250, 500];
const IDLE_RESTART_BACKOFF_MS = [0, 250, 500, 1000, 2000];
/** Heuristic only: Web Speech does not bound speechend→result delay. */
const SPEECH_END_GRACE_MS = 300;
export const DEFAULT_TRANSCRIPT_INACTIVITY_MS = 1800;
export const DEFAULT_NO_TEXT_CLOSE_MS = 4000;

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
  private idleEndStreak = 0;
  private utteranceEndStreak = 0;
  private restartTimer: ReturnType<typeof setTimeout> | null = null;
  private speechEndedPending = false;
  private speechEndGraceTimer: ReturnType<typeof setTimeout> | null = null;
  private transcriptInactivityTimer: ReturnType<typeof setTimeout> | null = null;
  private noTextTimer: ReturnType<typeof setTimeout> | null = null;
  private readonly transcriptInactivityMs: number;
  private readonly noTextCloseMs: number;
  private sawTranscriptText = false;
  private lastProgressText = "";
  private fatal = false;

  constructor(options?: { transcriptInactivityMs?: number; noTextCloseMs?: number }) {
    this.transcriptInactivityMs = options?.transcriptInactivityMs ?? DEFAULT_TRANSCRIPT_INACTIVITY_MS;
    this.noTextCloseMs = options?.noTextCloseMs ?? DEFAULT_NO_TEXT_CLOSE_MS;
  }

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
    this.idleEndStreak = 0;
    this.utteranceEndStreak = 0;
    this.speechEndedPending = false;
    this.clearSpeechEndGrace();
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

    this.stopNative(this.utteranceOpen);
    const native = new Ctor();
    native.continuous = true;
    native.interimResults = true;
    native.lang = this.language;
    native.onstart = () => undefined;
    native.onspeechstart = () => {
      if (this.generation !== generation) {
        return;
      }

      this.idleEndStreak = 0;
      this.utteranceEndStreak = 0;
      if (!this.utteranceOpen) {
        this.beginUtterance();
      }
    };
    native.onresult = (event) => {
      if (this.generation !== generation) {
        return;
      }

      const results = event.results;
      let sawFinal = false;
      let hadTranscriptProgress = false;
      for (let index = event.resultIndex; index < results.length; index += 1) {
        const result = results[index];
        const alternative = result?.[0];
        const text = alternative?.transcript?.trim() ?? "";
        if (!text) {
          continue;
        }

        hadTranscriptProgress = true;

        if (!this.utteranceOpen) {
          this.beginUtterance();
        }

        if (result.isFinal) {
          sawFinal = true;
        }

        this.revision += 1;
        this.noteTranscriptProgress(text);
        this.listener?.onEvidence({
          kind: result.isFinal ? "final" : "partial",
          utteranceId: this.utteranceId,
          revision: this.revision,
          text,
          confidence: alternative?.confidence
        });
      }

      if (hadTranscriptProgress) {
        this.idleEndStreak = 0;
        this.utteranceEndStreak = 0;
      }

      if (this.speechEndedPending) {
        this.clearSpeechEndGrace();
        if (sawFinal) {
          this.speechEndedPending = false;
          this.endUtterance();
        } else {
          this.scheduleSpeechEndGrace();
        }
      }
    };
    native.onspeechend = () => {
      if (this.generation !== generation) {
        return;
      }

      if (!this.utteranceOpen) {
        return;
      }

      this.speechEndedPending = true;
      this.scheduleSpeechEndGrace();
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

      this.clearSpeechEndGrace();
      if (this.speechEndedPending) {
        this.flushPendingUtteranceEnd();
      }
      this.native = null;
      if (!this.wantRunning || this.fatal) {
        return;
      }

      this.listener?.onRecognitionEnded?.();
      if (!this.wantRunning || this.fatal || this.generation !== generation) {
        return;
      }

      const backoffTable = this.utteranceOpen ? UTTERANCE_RESTART_BACKOFF_MS : IDLE_RESTART_BACKOFF_MS;
      const endStreak = this.utteranceOpen
        ? (this.utteranceEndStreak += 1)
        : (this.idleEndStreak += 1);
      const delay = backoffTable[Math.min(endStreak - 1, backoffTable.length - 1)] ?? backoffTable.at(-1) ?? 0;
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
      if (!this.speechEndedPending) {
        return;
      }

      this.flushPendingUtteranceEnd();
    }

    this.utteranceId = crypto.randomUUID();
    this.revision = 0;
    this.startedAt = performance.now();
    this.utteranceOpen = true;
    this.sawTranscriptText = false;
    this.lastProgressText = "";
    this.clearTranscriptEndpointTimers();
    this.scheduleNoTextTimer();
    this.listener?.onEvidence({ kind: "started", utteranceId: this.utteranceId, activityScore: 0.5 });
  }

  private noteTranscriptProgress(text: string): void {
    if (text === this.lastProgressText) {
      return;
    }

    this.lastProgressText = text;
    this.sawTranscriptText = true;
    this.clearNoTextTimer();
    this.scheduleTranscriptInactivityTimer();
  }

  private scheduleNoTextTimer(): void {
    this.clearNoTextTimer();
    this.noTextTimer = setTimeout(() => {
      this.noTextTimer = null;
      this.onNoTextTimeout();
    }, this.noTextCloseMs);
  }

  private scheduleTranscriptInactivityTimer(): void {
    this.clearTranscriptInactivityTimer();
    if (!this.utteranceOpen || !this.sawTranscriptText) {
      return;
    }

    this.transcriptInactivityTimer = setTimeout(() => {
      this.transcriptInactivityTimer = null;
      if (!this.utteranceOpen || !this.sawTranscriptText) {
        return;
      }

      this.endUtterance();
    }, this.transcriptInactivityMs);
  }

  private onNoTextTimeout(): void {
    if (!this.utteranceOpen || this.sawTranscriptText) {
      return;
    }

    this.discardNoiseUtterance();
  }

  private discardNoiseUtterance(): void {
    if (!this.utteranceOpen) {
      return;
    }

    this.speechEndedPending = false;
    this.clearSpeechEndGrace();
    this.clearTranscriptEndpointTimers();
    const utteranceId = this.utteranceId;
    this.listener?.onEvidence({ kind: "failed", utteranceId });
    this.utteranceOpen = false;
  }

  private clearNoTextTimer(): void {
    if (this.noTextTimer != null) {
      clearTimeout(this.noTextTimer);
      this.noTextTimer = null;
    }
  }

  private clearTranscriptInactivityTimer(): void {
    if (this.transcriptInactivityTimer != null) {
      clearTimeout(this.transcriptInactivityTimer);
      this.transcriptInactivityTimer = null;
    }
  }

  private clearTranscriptEndpointTimers(): void {
    this.clearNoTextTimer();
    this.clearTranscriptInactivityTimer();
  }

  private endUtterance(): void {
    if (!this.utteranceOpen) {
      return;
    }

    this.speechEndedPending = false;
    this.clearSpeechEndGrace();
    this.clearTranscriptEndpointTimers();
    const durationMs = Math.max(0, Math.round(performance.now() - this.startedAt));
    this.listener?.onEvidence({
      kind: "ended",
      utteranceId: this.utteranceId,
      durationMs,
      activityScore: 0.2
    });
    this.utteranceOpen = false;
  }

  private flushPendingUtteranceEnd(): void {
    if (!this.speechEndedPending || !this.utteranceOpen) {
      return;
    }

    this.speechEndedPending = false;
    this.clearSpeechEndGrace();
    this.endUtterance();
  }

  private scheduleSpeechEndGrace(): void {
    this.clearSpeechEndGrace();
    this.speechEndGraceTimer = setTimeout(() => {
      this.speechEndGraceTimer = null;
      if (!this.speechEndedPending || !this.utteranceOpen) {
        return;
      }

      this.speechEndedPending = false;
      this.endUtterance();
    }, SPEECH_END_GRACE_MS);
  }

  private stopNative(preserveUtteranceTimers = false): void {
    this.clearRestart();
    this.clearSpeechEndGrace();
    if (!preserveUtteranceTimers) {
      this.clearTranscriptEndpointTimers();
      this.lastProgressText = "";
    }

    this.speechEndedPending = false;
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

  private clearSpeechEndGrace(): void {
    if (this.speechEndGraceTimer != null) {
      clearTimeout(this.speechEndGraceTimer);
      this.speechEndGraceTimer = null;
    }
  }
}

export function createBrowserSpeechRecognizer(): BrowserSpeechRecognizer {
  return new BrowserSpeechRecognizer();
}
