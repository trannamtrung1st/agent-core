import type { SpeechVoice, SpeechVoiceHint } from "./clientSpeechSynthesizer";

export function speechLanguageCompatible(voiceLang: string, requested: string): boolean {
  const voice = voiceLang.trim().toLowerCase();
  const want = requested.trim().toLowerCase();
  if (!voice || !want) {
    return false;
  }

  if (voice === want) {
    return true;
  }

  const voiceBase = voice.split("-")[0] ?? voice;
  const wantBase = want.split("-")[0] ?? want;
  return voiceBase.length > 0 && voiceBase === wantBase;
}

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

  const lang = hint?.lang?.trim();
  if (lang) {
    const exact = voices.find((voice) => voice.lang.trim().toLowerCase() === lang.toLowerCase());
    if (exact) {
      return exact;
    }

    const compatible = voices.find((voice) => speechLanguageCompatible(voice.lang, lang));
    return compatible ?? null;
  }

  return voices.find((voice) => voice.default) ?? voices[0] ?? null;
}
