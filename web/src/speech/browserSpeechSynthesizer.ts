import { speechError } from "./errors";
import type {
  ClientSpeechSpeakRequest,
  ClientSpeechSynthesizer,
  ClientSpeechSynthesizerListener,
  SpeechVoice,
  SpeechVoiceHint
} from "./clientSpeechSynthesizer";
import { resolveSpeechVoice } from "./resolveSpeechVoice";

type NativeVoice = {
  voiceURI: string;
  name: string;
  lang: string;
  default: boolean;
};

function synthesisHost(): SpeechSynthesis | null {
  return typeof window !== "undefined" && "speechSynthesis" in window ? window.speechSynthesis : null;
}

function mapVoice(voice: NativeVoice): SpeechVoice {
  return {
    voiceURI: voice.voiceURI,
    name: voice.name,
    lang: voice.lang,
    default: voice.default
  };
}

export class BrowserSpeechSynthesizer implements ClientSpeechSynthesizer {
  readonly adapterId = "browser" as const;
  private speaking: SpeechSynthesisUtterance | null = null;
  private cancelled = false;

  listVoices(): SpeechVoice[] {
    const synth = synthesisHost();
    if (!synth) {
      return [];
    }

    return Array.from(synth.getVoices(), (voice) => mapVoice(voice));
  }

  resolveVoice(hint?: SpeechVoiceHint): SpeechVoice | null {
    return resolveSpeechVoice(this.listVoices(), hint);
  }

  async speak(request: ClientSpeechSpeakRequest, listener?: ClientSpeechSynthesizerListener): Promise<void> {
    const synth = synthesisHost();
    if (!synth || typeof SpeechSynthesisUtterance === "undefined") {
      const error = speechError("SpeechSynthesisUnavailable");
      listener?.onError?.(error);
      throw error;
    }

    const hint = request.hint?.lang
      ? request.hint
      : { ...request.hint, lang: request.language };
    const voices = this.listVoices();
    const chosen = this.resolveVoice(hint);
    const langConstraint = Boolean(request.language?.trim() || request.hint?.lang?.trim());
    if (langConstraint && voices.length > 0 && !chosen) {
      const error = speechError("SpeechVoiceUnavailable");
      listener?.onError?.(error);
      throw error;
    }
    this.cancelled = false;
    const utterance = new SpeechSynthesisUtterance(request.text);
    if (request.language) {
      utterance.lang = request.language;
    }
    if (typeof request.speakingRate === "number" && Number.isFinite(request.speakingRate)) {
      utterance.rate = request.speakingRate;
    }
    if (chosen) {
      const native = synth.getVoices().find((voice) => voice.voiceURI === chosen.voiceURI);
      if (native) {
        utterance.voice = native;
      }
    }

    this.speaking = utterance;
    await new Promise<void>((resolve, reject) => {
      utterance.onend = () => {
        if (this.speaking === utterance) {
          this.speaking = null;
        }
        listener?.onEnd?.();
        resolve();
      };
      utterance.onerror = (event) => {
        if (this.speaking === utterance) {
          this.speaking = null;
        }
        const interrupted = this.cancelled
          || event.error === "interrupted"
          || event.error === "canceled";
        if (interrupted) {
          resolve();
          return;
        }

        const error = speechError("SpeechPlaybackFailed");
        listener?.onError?.(error);
        reject(error);
      };
      synth.speak(utterance);
    });
  }

  async cancel(): Promise<void> {
    this.cancelled = true;
    this.speaking = null;
    const synth = synthesisHost();
    try {
      synth?.cancel();
    } catch {
      // ignored
    }
  }
}

export function createBrowserSpeechSynthesizer(): BrowserSpeechSynthesizer {
  return new BrowserSpeechSynthesizer();
}

export function browserSpeechSynthesisSupported(): boolean {
  return synthesisHost() !== null && typeof SpeechSynthesisUtterance !== "undefined";
}
