import { describe, expect, it } from "vitest";
import { FakeSpeechRecognizer, FakeSpeechSynthesizer } from "./fakeSpeechAdapters";
import { sessionErrorClass } from "../features/chat/sessionError";
import { speechError } from "./errors";

describe("FakeSpeechRecognizer", () => {
  it("starts, emits partials and finals, stops, and cancels without Web Speech", async () => {
    const fake = new FakeSpeechRecognizer();
    const seen: string[] = [];
    const errors: string[] = [];
    await fake.start({
      onEvidence: (evidence) => seen.push(evidence.kind),
      onError: (error) => errors.push(error.code)
    });
    expect(fake.isRunning).toBe(true);
    fake.emit({ kind: "started", utteranceId: "u1", activityScore: 0.4 });
    fake.emit({ kind: "partial", utteranceId: "u1", revision: 1, text: "hel" });
    fake.emit({ kind: "final", utteranceId: "u1", text: "hello", confidence: 0.9 });
    await fake.stop();
    expect(fake.isRunning).toBe(false);
    expect(seen).toEqual(["started", "partial", "final", "ended"]);

    await fake.start({
      onEvidence: (evidence) => seen.push(evidence.kind),
      onError: (error) => errors.push(error.code)
    });
    await fake.cancel();
    expect(fake.isRunning).toBe(false);
    expect(seen.includes("ended")).toBe(true);
    expect(errors).toEqual([]);
  });

  it("fails with a structured P0 speech error and a failed evidence kind", async () => {
    const fake = new FakeSpeechRecognizer();
    const errors: string[] = [];
    const kinds: string[] = [];
    await fake.start({
      onEvidence: (evidence) => kinds.push(evidence.kind),
      onError: (error) => errors.push(error.code)
    });
    fake.fail("SpeechRecognitionUnavailable", "u-fail");
    expect(errors).toEqual(["SpeechRecognitionUnavailable"]);
    expect(kinds).toEqual(["failed"]);
    expect(sessionErrorClass("Speech", "SpeechUnsupported")).toBe("speech/capture/playback");
    expect(speechError("SpeechPermissionDenied").classId).toBe("speech/capture/playback");
    expect(speechError("SpeechDeviceUnavailable").fatal).toBe(false);
    expect(speechError("SpeechRecognitionRestartLimit").code).toBe("SpeechRecognitionRestartLimit");
  });
});

describe("FakeSpeechSynthesizer", () => {
  it("cancel releases a hold without invoking onEnd", async () => {
    const synth = new FakeSpeechSynthesizer();
    synth.armHold();
    let ended = false;
    const speaking = synth.speak({ text: "Hi" }, { onEnd: () => {
      ended = true;
    } });
    await synth.cancel();
    await speaking;
    expect(ended).toBe(false);
    expect(synth.cancelled).toBe(true);
  });
});
