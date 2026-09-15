import { describe, expect, it } from "vitest";
import { EARLY_AUDIO_MS, MAX_QUEUED_SAMPLES, OutputAudioGate } from "./outputAdmission";

const empty = new Uint8Array(960);

describe("output audio admission", () => {
  it("buffers unknown response audio and plays after started", () => {
    const gate = new OutputAudioGate();
    const expected = {
      sessionId: "s1",
      attachmentId: "a1",
      tombstones: {},
      stopped: new Set<string>(),
      queuedSamples: 0
    };
    const frame = {
      sessionId: "s1",
      attachmentId: "a1",
      responseId: "r1",
      frameSequence: 1,
      sampleOffset: 0,
      data: empty
    };
    expect(gate.admit(frame, expected)).toBe("buffer");
    gate.markStarted("r1");
    expect(gate.commitBuffered(frame, 0)).toBe(true);
  });

  it("rejects stale identity, sequence gaps, and cumulative queue overflow", () => {
    const gate = new OutputAudioGate();
    gate.markStarted("r1");
    const expected = {
      sessionId: "s1",
      attachmentId: "a1",
      tombstones: {},
      stopped: new Set<string>(),
      queuedSamples: 0
    };
    expect(
      gate.admit(
        { sessionId: "other", attachmentId: "a1", responseId: "r1", frameSequence: 1, sampleOffset: 0, data: empty },
        expected
      )
    ).toBe("reject");
    expect(
      gate.admit(
        { sessionId: "s1", attachmentId: "a1", responseId: "r1", frameSequence: 1, sampleOffset: 0, data: empty },
        { ...expected, tombstones: { r1: "interrupted" } }
      )
    ).toBe("reject");
    expect(gate.admit({ sessionId: "s1", attachmentId: "a1", responseId: "r1", frameSequence: 1, sampleOffset: 0, data: empty }, expected)).toBe(
      "play"
    );
    expect(gate.admit({ sessionId: "s1", attachmentId: "a1", responseId: "r1", frameSequence: 3, sampleOffset: 480, data: empty }, expected)).toBe(
      "reject"
    );
    expect(
      gate.admit(
        { sessionId: "s1", attachmentId: "a1", responseId: "r1", frameSequence: 2, sampleOffset: 480, data: empty },
        { ...expected, queuedSamples: MAX_QUEUED_SAMPLES }
      )
    ).toBe("reject");
  });
});

describe("early audio window", () => {
  it("is 60 ms", () => {
    expect(EARLY_AUDIO_MS).toBe(60);
  });
});
