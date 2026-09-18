import type { SessionErrorView } from "../features/chat/sessionError";
import type {
  ClientSpeechEvidence,
  ClientSpeechRecognizer,
  ClientSpeechRecognizerListener,
  SpeechInputTransport,
  SpeechOutputTransport
} from "./clientSpeechRecognizer";

export type SpeechTransportHooks = {
  onEvidence?: (evidence: ClientSpeechEvidence) => void;
  onError?: (error: SessionErrorView) => void;
};

export type SpeechTransportService = {
  setInputRecognizer: (recognizer: ClientSpeechRecognizer | null) => void;
  inputRecognizerAdapterId: () => "browser" | "fake" | null;
  setActiveInputTransport: (transport: SpeechInputTransport | null) => void;
  activeInputTransport: () => SpeechInputTransport | null;
  setActiveOutputTransport: (transport: SpeechOutputTransport | null) => void;
  activeOutputTransport: () => SpeechOutputTransport | null;
  startInput: (hooks?: SpeechTransportHooks) => Promise<void>;
  stopInput: () => Promise<void>;
  cancelInput: () => Promise<void>;
};

export function createSpeechTransportService(
  inputRecognizer: ClientSpeechRecognizer | null = null
): SpeechTransportService {
  let recognizer = inputRecognizer;
  let inputTransport: SpeechInputTransport | null = null;
  let outputTransport: SpeechOutputTransport | null = null;
  let started = false;

  const service: SpeechTransportService = {
    setInputRecognizer(next) {
      recognizer = next;
    },
    inputRecognizerAdapterId() {
      return recognizer?.adapterId ?? null;
    },
    setActiveInputTransport(transport) {
      inputTransport = transport;
    },
    activeInputTransport() {
      return inputTransport;
    },
    setActiveOutputTransport(transport) {
      outputTransport = transport;
    },
    activeOutputTransport() {
      return outputTransport;
    },
    async startInput(hooks = {}) {
      if (inputTransport !== "clientTranscript" || !recognizer) {
        started = false;
        return;
      }

      const listener: ClientSpeechRecognizerListener = {
        onEvidence: (evidence) => hooks.onEvidence?.(evidence),
        onError: (error) => hooks.onError?.(error)
      };
      await recognizer.start(listener);
      started = true;
    },
    async stopInput() {
      if (!started) {
        return;
      }

      started = false;
      await recognizer?.stop();
    },
    async cancelInput() {
      if (!started) {
        return;
      }

      started = false;
      await recognizer?.cancel();
    }
  };
  return service;
}

export const speechTransport = createSpeechTransportService();
