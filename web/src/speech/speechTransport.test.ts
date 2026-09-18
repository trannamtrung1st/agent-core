import { describe, expect, it } from "vitest";
import { FakeSpeechRecognizer } from "./fakeSpeechAdapters";
import { createSpeechTransportService } from "./speechTransport";

describe("speechTransport", () => {
  it("owns the active input transport and drives the injected fake recognizer", async () => {
    const fake = new FakeSpeechRecognizer();
    const transport = createSpeechTransportService(fake);
    const kinds: string[] = [];
    transport.setActiveInputTransport("clientTranscript");
    transport.setActiveOutputTransport("serverAudio");
    expect(transport.activeInputTransport()).toBe("clientTranscript");
    expect(transport.activeOutputTransport()).toBe("serverAudio");
    expect(transport.inputRecognizerAdapterId()).toBe("fake");
    await transport.startInput({
      onEvidence: (evidence) => kinds.push(evidence.kind)
    });
    fake.emit({ kind: "started", utteranceId: "u1" });
    fake.emit({ kind: "partial", utteranceId: "u1", revision: 1, text: "hi" });
    await transport.stopInput();
    expect(kinds).toEqual(["started", "partial", "ended"]);
    expect(fake.isRunning).toBe(false);
  });

  it("does not start a recognizer on serverAudio input", async () => {
    const fake = new FakeSpeechRecognizer();
    const transport = createSpeechTransportService(fake);
    transport.setActiveInputTransport("serverAudio");
    await transport.startInput();
    expect(fake.isRunning).toBe(false);
    await transport.cancelInput();
    expect(fake.isRunning).toBe(false);
  });

  it("cancel stops the fake without emitting a final", async () => {
    const fake = new FakeSpeechRecognizer();
    const transport = createSpeechTransportService();
    transport.setInputRecognizer(fake);
    transport.setActiveInputTransport("clientTranscript");
    const kinds: string[] = [];
    await transport.startInput({
      onEvidence: (evidence) => kinds.push(evidence.kind)
    });
    fake.emit({ kind: "partial", utteranceId: "u2", revision: 1, text: "nope" });
    await transport.cancelInput();
    expect(kinds).toEqual(["partial"]);
    expect(fake.isRunning).toBe(false);
  });
});
