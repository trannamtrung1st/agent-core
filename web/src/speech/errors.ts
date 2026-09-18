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

export function speechError(
  code: SpeechErrorCode,
  message?: string,
  extras?: Pick<SessionErrorView, "extensions">
): SessionErrorView {
  return sessionErrorFromMessage(message ?? MESSAGES[code], {
    category: "Speech",
    code,
    fatal: false,
    extensions: extras?.extensions
  });
}

function safeRecognitionError(raw: string | undefined): string {
  const trimmed = raw?.trim() ?? "";
  if (/^[a-z0-9-]{1,64}$/i.test(trimmed)) {
    return trimmed;
  }

  return trimmed ? "unrecognized" : "unknown";
}

export function speechErrorFromRecognitionError(raw: string | undefined): SessionErrorView {
  switch (raw) {
    case "not-allowed":
      return speechError("SpeechPermissionDenied");
    case "audio-capture":
      return speechError("SpeechDeviceUnavailable");
    case "network":
      return speechError(
        "SpeechRecognitionUnavailable",
        "Speech recognition service could not be reached.",
        { extensions: { recognitionError: "network" } }
      );
    case "service-not-allowed":
      return speechError(
        "SpeechRecognitionUnavailable",
        "Speech recognition is not allowed in this browser profile.",
        { extensions: { recognitionError: "service-not-allowed" } }
      );
    default:
      return speechError("SpeechRecognitionUnavailable", undefined, {
        extensions: { recognitionError: safeRecognitionError(raw) }
      });
  }
}
