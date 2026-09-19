import { afterEach, describe, expect, it, vi } from "vitest";
import { FakeSpeechSynthesizer } from "./fakeSpeechAdapters";
import { resolveSpeechVoice, voiceHintFromSegment } from "./resolveSpeechVoice";
import { speechError } from "./errors";
import { createSpeechTransportService } from "./speechTransport";
import { BrowserSpeechSynthesizer } from "./browserSpeechSynthesizer";

const voices = [
  { voiceURI: "uri-en", name: "English", lang: "en-US", default: true },
  { voiceURI: "uri-fr", name: "French", lang: "fr-FR" },
  { voiceURI: "uri-en-gb", name: "British", lang: "en-GB" }
];

describe("voiceHintFromSegment", () => {
  it("treats application default alias as locale-only selection", () => {
    expect(voiceHintFromSegment("default", "en")).toEqual({ lang: "en" });
    expect(voiceHintFromSegment("DEFAULT", "fr-FR")).toEqual({ lang: "fr-FR" });
    expect(resolveSpeechVoice(voices, voiceHintFromSegment("default", "en-GB"))?.name).toBe("British");
  });

  it("passes concrete voice names through to resolution", () => {
    expect(voiceHintFromSegment("British", "en")).toEqual({ name: "British", lang: "en" });
    expect(resolveSpeechVoice(voices, voiceHintFromSegment("British", "en"))?.voiceURI).toBe("uri-en-gb");
  });
});

describe("resolveSpeechVoice", () => {
  it("resolves voiceURI, then name, then exact or base language, and does not fall through to English", () => {
    expect(resolveSpeechVoice(voices, { voiceURI: "uri-fr" })?.name).toBe("French");
    expect(resolveSpeechVoice(voices, { voiceURI: "missing", name: "British" })?.name).toBe("British");
    expect(resolveSpeechVoice(voices, { lang: "fr-FR" })?.name).toBe("French");
    expect(resolveSpeechVoice(voices, { lang: "en" })?.name).toBe("English");
    expect(resolveSpeechVoice(voices, { lang: "en-GB" })?.name).toBe("British");
    expect(resolveSpeechVoice(voices, { voiceURI: "nope", name: "nope", lang: "nope" })).toBeNull();
    expect(resolveSpeechVoice(voices, { lang: "ja-JP" })).toBeNull();
    expect(resolveSpeechVoice(voices)?.name).toBe("English");
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
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("fails closed when speechSynthesis is missing", async () => {
    vi.stubGlobal("speechSynthesis", undefined);
    vi.stubGlobal("SpeechSynthesisUtterance", undefined);
    const adapter = new BrowserSpeechSynthesizer();
    const errors: string[] = [];
    await expect(
      adapter.speak({ text: "x" }, { onError: (error) => errors.push(error.code) })
    ).rejects.toMatchObject({ code: "SpeechSynthesisUnavailable" });
    expect(errors).toEqual(["SpeechSynthesisUnavailable"]);
  });

  it("resolves speak on utterance end and does not cancel the previous segment", async () => {
    class MockUtterance {
      lang = "";
      rate = 1;
      voice: SpeechSynthesisVoice | null = null;
      onend: ((event: Event) => void) | null = null;
      onerror: ((event: { error?: string }) => void) | null = null;
      constructor(public text: string) {}
    }
    const spoken: MockUtterance[] = [];
    const cancel = vi.fn();
    vi.stubGlobal("SpeechSynthesisUtterance", MockUtterance);
    vi.stubGlobal("speechSynthesis", {
      getVoices: () => [],
      speak(utterance: MockUtterance) {
        spoken.push(utterance);
      },
      cancel
    });
    const adapter = new BrowserSpeechSynthesizer();
    const first = adapter.speak({ text: "one", language: "en", speakingRate: 1.25 });
    expect(spoken).toHaveLength(1);
    expect(spoken[0]?.lang).toBe("en");
    expect(spoken[0]?.rate).toBe(1.25);
    expect(cancel).not.toHaveBeenCalled();
    spoken[0]?.onend?.(new Event("end"));
    await first;
    const second = adapter.speak({ text: "two", language: "en", speakingRate: 1 });
    expect(spoken).toHaveLength(2);
    expect(cancel).not.toHaveBeenCalled();
    spoken[1]?.onend?.(new Event("end"));
    await second;
    expect(spoken.map((item) => item.text)).toEqual(["one", "two"]);
  });

  it("fails Voice when voices exist but none match the requested locale", async () => {
    class MockUtterance {
      lang = "";
      rate = 1;
      voice: SpeechSynthesisVoice | null = null;
      onend: ((event: Event) => void) | null = null;
      onerror: ((event: { error?: string }) => void) | null = null;
      constructor(public text: string) {}
    }
    const spoken: MockUtterance[] = [];
    vi.stubGlobal("SpeechSynthesisUtterance", MockUtterance);
    vi.stubGlobal("speechSynthesis", {
      getVoices: () => [
        { voiceURI: "en", name: "English", lang: "en-US", default: true, localService: true }
      ],
      speak(utterance: MockUtterance) {
        spoken.push(utterance);
      },
      cancel: vi.fn()
    });
    const adapter = new BrowserSpeechSynthesizer();
    const errors: string[] = [];
    await expect(
      adapter.speak({ text: "hola", language: "es-ES" }, { onError: (error) => errors.push(error.code) })
    ).rejects.toMatchObject({ code: "SpeechVoiceUnavailable" });
    expect(errors).toEqual(["SpeechVoiceUnavailable"]);
    expect(spoken).toEqual([]);
  });

  it("cancels only on explicit cancel", async () => {
    class MockUtterance {
      lang = "";
      rate = 1;
      voice: SpeechSynthesisVoice | null = null;
      onend: ((event: Event) => void) | null = null;
      onerror: ((event: { error?: string }) => void) | null = null;
      constructor(public text: string) {}
    }
    const cancel = vi.fn(() => {
      spoken[0]?.onerror?.({ error: "interrupted" });
    });
    const spoken: MockUtterance[] = [];
    vi.stubGlobal("SpeechSynthesisUtterance", MockUtterance);
    vi.stubGlobal("speechSynthesis", {
      getVoices: () => [],
      speak(utterance: MockUtterance) {
        spoken.push(utterance);
      },
      cancel
    });
    const adapter = new BrowserSpeechSynthesizer();
    const pending = adapter.speak({ text: "hold" });
    await adapter.cancel();
    await pending;
    expect(cancel).toHaveBeenCalled();
  });
});
