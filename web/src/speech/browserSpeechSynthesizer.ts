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

    const chosen = this.resolveVoice(request.hint);
    this.cancelNative();
    const utterance = new SpeechSynthesisUtterance(request.text);
    if (chosen) {
      const native = synth.getVoices().find((voice) => voice.voiceURI === chosen.voiceURI);
      if (native) {
        utterance.voice = native;
      }
    }

    utterance.onend = () => {
      listener?.onEnd?.();
    };
    utterance.onerror = () => {
      listener?.onError?.(speechError("SpeechPlaybackFailed"));
    };
    synth.speak(utterance);
  }

  async cancel(): Promise<void> {
    this.cancelNative();
  }

  private cancelNative(): void {
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
