import type { SpeechVoice, SpeechVoiceHint } from "./clientSpeechSynthesizer";

export function resolveSpeechVoice(voices: readonly SpeechVoice[], hint?: SpeechVoiceHint): SpeechVoice | null {
  if (voices.length === 0) {
    return null;
  }

  if (hint?.voiceURI) {
    const byUri = voices.find((voice) => voice.voiceURI === hint.voiceURI);
    if (byUri) {
      return byUri;
    }
  }

  if (hint?.name) {
    const byName = voices.find((voice) => voice.name === hint.name);
    if (byName) {
      return byName;
    }
  }

  if (hint?.lang) {
    const exact = voices.find((voice) => voice.lang === hint.lang);
    if (exact) {
      return exact;
    }

    const prefix = hint.lang.split("-")[0] ?? hint.lang;
    const byPrefix = voices.find((voice) => voice.lang === prefix || voice.lang.startsWith(`${prefix}-`));
    if (byPrefix) {
      return byPrefix;
    }
  }

  return voices.find((voice) => voice.default) ?? voices[0] ?? null;
}
