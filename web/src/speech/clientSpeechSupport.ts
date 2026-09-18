import { browserSpeechRecognitionSupported } from "./browserSpeechRecognizer";
import { browserSpeechSynthesisSupported } from "./browserSpeechSynthesizer";
import type { ClientSpeechRecognizer } from "./clientSpeechRecognizer";
import type { ClientSpeechSynthesizer } from "./clientSpeechSynthesizer";

export type AgentCoreSpeechTestSeam = {
  fakeRecognizer?: boolean;
  fakeSynthesizer?: boolean;
  recognizer?: ClientSpeechRecognizer;
  synthesizer?: ClientSpeechSynthesizer;
};

export function speechTestSeam(): AgentCoreSpeechTestSeam {
  if (typeof window === "undefined") {
    return {};
  }

  return (window as Window & { __agentCoreSpeechTest?: AgentCoreSpeechTestSeam }).__agentCoreSpeechTest ?? {};
}

export function clientRecognitionSupported(): boolean {
  const seam = speechTestSeam();
  return Boolean(seam.fakeRecognizer || seam.recognizer) || browserSpeechRecognitionSupported();
}

export function clientSynthesisSupported(): boolean {
  const seam = speechTestSeam();
  return Boolean(seam.fakeSynthesizer || seam.synthesizer) || browserSpeechSynthesisSupported();
}
