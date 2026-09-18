import { describe, expect, it } from "vitest";
import { FakeSpeechSynthesizer } from "./fakeSpeechAdapters";
import { resolveSpeechVoice } from "./resolveSpeechVoice";
import { speechError } from "./errors";
import { createSpeechTransportService } from "./speechTransport";
import { BrowserSpeechSynthesizer } from "./browserSpeechSynthesizer";

const voices = [
  { voiceURI: "uri-en", name: "English", lang: "en-US", default: true },
  { voiceURI: "uri-fr", name: "French", lang: "fr-FR" },
  { voiceURI: "uri-en-gb", name: "British", lang: "en-GB" }
];

describe("resolveSpeechVoice", () => {
  it("resolves voiceURI, then name, then language, then default", () => {
    expect(resolveSpeechVoice(voices, { voiceURI: "uri-fr" })?.name).toBe("French");
    expect(resolveSpeechVoice(voices, { voiceURI: "missing", name: "British" })?.name).toBe("British");
    expect(resolveSpeechVoice(voices, { lang: "fr-FR" })?.name).toBe("French");
    expect(resolveSpeechVoice(voices, { lang: "en" })?.name).toBe("English");
    expect(resolveSpeechVoice(voices, { voiceURI: "nope", name: "nope", lang: "nope" })?.default).toBe(true);
  });
});

describe("FakeSpeechSynthesizer", () => {
  it("speaks without a backend speech key and records the resolved voice", async () => {
    const fake = new FakeSpeechSynthesizer(voices);
    await fake.speak({ text: "Hello", hint: { name: "French" } });
    expect(fake.spoken).toEqual([{ text: "Hello", voice: voices[1] }]);
    await fake.cancel();
    expect(fake.cancelled).toBe(true);
    expect(speechError("SpeechSynthesisUnavailable").classId).toBe("speech/capture/playback");
    expect(speechError("SpeechPlaybackFailed").code).toBe("SpeechPlaybackFailed");
  });
});

describe("speechTransport output", () => {
  it("extends the same service to own clientSpeech output", async () => {
    const fake = new FakeSpeechSynthesizer(voices);
    const transport = createSpeechTransportService(null, fake);
    transport.setActiveOutputTransport("clientSpeech");
    expect(transport.outputSynthesizerAdapterId()).toBe("fake");
    expect(transport.resolveOutputVoice({ name: "French" })?.voiceURI).toBe("uri-fr");
    await transport.speakOutput({ text: "Hi" });
    expect(fake.spoken[0]?.text).toBe("Hi");
    transport.setActiveOutputTransport("serverAudio");
    await transport.speakOutput({ text: "skip" });
    expect(fake.spoken).toHaveLength(1);
  });
});

describe("BrowserSpeechSynthesizer", () => {
  it("fails closed when speechSynthesis is missing", async () => {
    const adapter = new BrowserSpeechSynthesizer();
    const errors: string[] = [];
    await expect(
      adapter.speak({ text: "x" }, { onError: (error) => errors.push(error.code) })
    ).rejects.toMatchObject({ code: "SpeechSynthesisUnavailable" });
    expect(errors).toEqual(["SpeechSynthesisUnavailable"]);
  });
});
