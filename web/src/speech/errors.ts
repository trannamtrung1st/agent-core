import { sessionErrorFromMessage, type SessionErrorView } from "../features/chat/sessionError";

export const SPEECH_ERROR_CODES = [
  "SpeechUnsupported",
  "SpeechPermissionDenied",
  "SpeechDeviceUnavailable",
  "SpeechRecognitionUnavailable",
  "SpeechRecognitionRestartLimit",
  "SpeechSynthesisUnavailable",
  "SpeechVoiceUnavailable",
  "SpeechPlaybackFailed"
] as const;

export type SpeechErrorCode = (typeof SPEECH_ERROR_CODES)[number];

const MESSAGES: Record<SpeechErrorCode, string> = {
  SpeechUnsupported: "Speech recognition is not available in this browser.",
  SpeechPermissionDenied: "Microphone permission was denied.",
  SpeechDeviceUnavailable: "No speech input device is available.",
  SpeechRecognitionUnavailable: "Speech recognition is unavailable.",
  SpeechRecognitionRestartLimit: "Speech recognition restarted too many times.",
  SpeechSynthesisUnavailable: "Speech synthesis is not available in this browser.",
  SpeechVoiceUnavailable: "No matching speech synthesis voice is available.",
  SpeechPlaybackFailed: "Speech playback failed."
};

export function speechError(code: SpeechErrorCode, message?: string): SessionErrorView {
  return sessionErrorFromMessage(message ?? MESSAGES[code], {
    category: "Speech",
    code,
    fatal: false
  });
}

export function speechErrorFromRecognitionError(raw: string | undefined): SessionErrorView {
  switch (raw) {
    case "not-allowed":
      return speechError("SpeechPermissionDenied");
    case "audio-capture":
      return speechError("SpeechDeviceUnavailable");
    case "service-not-allowed":
    case "network":
    case "no-speech":
      return speechError("SpeechRecognitionUnavailable");
    default:
      return speechError("SpeechRecognitionUnavailable");
  }
}
