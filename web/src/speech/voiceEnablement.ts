import { browserSpeechRecognitionSupported } from "./browserSpeechRecognizer";
import { browserSpeechSynthesisSupported } from "./browserSpeechSynthesizer";
import type { SpeechInputTransport, SpeechOutputTransport } from "./clientSpeechRecognizer";

export type VoiceEnablementInput = {
  voiceAvailable: boolean;
  inputTransport: SpeechInputTransport | null;
  outputTransport: SpeechOutputTransport | null;
  recognitionSupported?: boolean;
  synthesisSupported?: boolean;
};

export function voiceControlEnabled(input: VoiceEnablementInput): boolean {
  if (!input.voiceAvailable) {
    return false;
  }

  const recognitionOk = input.recognitionSupported ?? browserSpeechRecognitionSupported();
  const synthesisOk = input.synthesisSupported ?? browserSpeechSynthesisSupported();
  if (input.inputTransport === "clientTranscript" && !recognitionOk) {
    return false;
  }

  if (input.outputTransport === "clientSpeech" && !synthesisOk) {
    return false;
  }

  return true;
}
