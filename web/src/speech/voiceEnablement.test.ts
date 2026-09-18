import { describe, expect, it } from "vitest";
import { voiceControlEnabled } from "./voiceEnablement";

describe("voiceControlEnabled", () => {
  it("ANDs server voiceAvailable with clientTranscript SpeechRecognition detection", () => {
    expect(voiceControlEnabled({
      voiceAvailable: true,
      inputTransport: "clientTranscript",
      outputTransport: "serverAudio",
      recognitionSupported: true,
      synthesisSupported: false
    })).toBe(true);
    expect(voiceControlEnabled({
      voiceAvailable: true,
      inputTransport: "clientTranscript",
      outputTransport: "serverAudio",
      recognitionSupported: false,
      synthesisSupported: true
    })).toBe(false);
  });

  it("ANDs server voiceAvailable with clientSpeech speechSynthesis detection", () => {
    expect(voiceControlEnabled({
      voiceAvailable: true,
      inputTransport: "serverAudio",
      outputTransport: "clientSpeech",
      recognitionSupported: false,
      synthesisSupported: true
    })).toBe(true);
    expect(voiceControlEnabled({
      voiceAvailable: true,
      inputTransport: "serverAudio",
      outputTransport: "clientSpeech",
      recognitionSupported: true,
      synthesisSupported: false
    })).toBe(false);
  });

  it("keeps text-only when voiceAvailable is false", () => {
    expect(voiceControlEnabled({
      voiceAvailable: false,
      inputTransport: "serverAudio",
      outputTransport: "serverAudio",
      recognitionSupported: true,
      synthesisSupported: true
    })).toBe(false);
  });
});
