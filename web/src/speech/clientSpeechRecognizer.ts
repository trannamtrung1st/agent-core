import type { SessionErrorView } from "../features/chat/sessionError";

export type SpeechInputTransport = "serverAudio" | "clientTranscript";
export type SpeechOutputTransport = "serverAudio" | "clientSpeech";

export type ClientSpeechEvidenceKind = "started" | "partial" | "final" | "ended" | "failed";

export type ClientSpeechEvidence = {
  kind: ClientSpeechEvidenceKind;
  utteranceId: string;
  revision?: number;
  text?: string;
  confidence?: number;
  activityScore?: number;
  durationMs?: number;
};

export type ClientSpeechRecognizerListener = {
  onEvidence: (evidence: ClientSpeechEvidence) => void;
  onError: (error: SessionErrorView) => void;
  onRecognitionEnded?: () => void;
};

export type ClientSpeechRecognizerStartOptions = {
  language?: string;
};

export type ClientSpeechRecognizer = {
  readonly adapterId: "browser" | "fake";
  start: (
    listener: ClientSpeechRecognizerListener,
    options?: ClientSpeechRecognizerStartOptions
  ) => Promise<void>;
  stop: () => Promise<void>;
  cancel: () => Promise<void>;
};
