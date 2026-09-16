import { describe, expect, it } from "vitest";
import { EARLY_AUDIO_MS, MAX_QUEUED_SAMPLES, OutputAudioGate } from "./outputAdmission";

const empty = new Uint8Array(960);
const overflow = new Uint8Array((MAX_QUEUED_SAMPLES - 959) * 2);

describe("output audio admission", () => {
  it("buffers contiguous pre-start frames in the 60 ms window and rejects cumulative overflow", () => {
    const gate = new OutputAudioGate();
    const expected = {
      sessionId: "s1",
      attachmentId: "a1",
      tombstones: {},
      stopped: new Set<string>(),
      queuedSamples: 0
    };
    const first = {
      sessionId: "s1",
      attachmentId: "a1",
      responseId: "r1",
      frameSequence: 1,
      sampleOffset: 0,
      data: empty
    };
    const second = {
      sessionId: "s1",
      attachmentId: "a1",
      responseId: "r1",
      frameSequence: 2,
      sampleOffset: 480,
      data: empty
    };
    expect(EARLY_AUDIO_MS).toBe(60);
    expect(gate.admit(first, expected)).toBe("buffer");
    expect(gate.admit(second, expected)).toBe("buffer");
    expect(gate.queuedBuffered()).toBe(960);
    expect(
      gate.admit(
        {
          sessionId: "s1",
          attachmentId: "a1",
          responseId: "r1",
          frameSequence: 3,
          sampleOffset: 960,
          data: overflow
        },
        expected
      )
    ).toBe("overflow");
    gate.markStarted("r1");
    expect(gate.commitBuffered(first, 0)).toBe(true);
    expect(gate.commitBuffered(second, 480)).toBe(true);
  });

  it("rejects stale identity, sequence gaps, and reports overflow separately", () => {
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
    ).toBe("overflow");
  });

  it("expiry drops gate state so a later prefix-skipping frame is rejected", () => {
    const gate = new OutputAudioGate();
    const expected = {
      sessionId: "s1",
      attachmentId: "a1",
      tombstones: {},
      stopped: new Set<string>(),
      queuedSamples: 0
    };
    expect(
      gate.admit(
        { sessionId: "s1", attachmentId: "a1", responseId: "r1", frameSequence: 1, sampleOffset: 0, data: empty },
        expected
      )
    ).toBe("buffer");
    expect(gate.queuedBuffered()).toBe(480);
    gate.drop("r1");
    expect(gate.queuedBuffered()).toBe(0);
    expect(
      gate.admit(
        { sessionId: "s1", attachmentId: "a1", responseId: "r1", frameSequence: 2, sampleOffset: 480, data: empty },
        expected
      )
    ).toBe("reject");
  });

  it("does not count buffered samples twice when enforcing the queue limit", () => {
    const gate = new OutputAudioGate();
    const expected = {
      sessionId: "s1",
      attachmentId: "a1",
      tombstones: {},
      stopped: new Set<string>(),
      queuedSamples: MAX_QUEUED_SAMPLES - 960
    };
    expect(
      gate.admit(
        { sessionId: "s1", attachmentId: "a1", responseId: "r1", frameSequence: 1, sampleOffset: 0, data: empty },
        expected
      )
    ).toBe("buffer");
    expect(
      gate.admit(
        { sessionId: "s1", attachmentId: "a1", responseId: "r1", frameSequence: 2, sampleOffset: 480, data: empty },
        expected
      )
    ).toBe("buffer");
  });
});
