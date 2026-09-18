import type { SessionErrorView } from "../features/chat/sessionError";

export type SpeechVoiceHint = {
  voiceURI?: string;
  name?: string;
  lang?: string;
};

export type SpeechVoice = {
  voiceURI: string;
  name: string;
  lang: string;
  default?: boolean;
};

export type ClientSpeechSpeakRequest = {
  text: string;
  hint?: SpeechVoiceHint;
};

export type ClientSpeechSynthesizerListener = {
  onEnd?: () => void;
  onError?: (error: SessionErrorView) => void;
};

export type ClientSpeechSynthesizer = {
  readonly adapterId: "browser" | "fake";
  listVoices: () => SpeechVoice[];
  resolveVoice: (hint?: SpeechVoiceHint) => SpeechVoice | null;
  speak: (request: ClientSpeechSpeakRequest, listener?: ClientSpeechSynthesizerListener) => Promise<void>;
  cancel: () => Promise<void>;
};
