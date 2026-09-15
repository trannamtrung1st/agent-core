import { describe, expect, it } from "vitest";
import { VoiceActivityObserver } from "./vad";

describe("vad hysteresis", () => {
  it("requires start hang and end hang before emitting boundaries", () => {
    const vad = new VoiceActivityObserver({
      startThreshold: 0.7,
      endThreshold: 0.4,
      startHangFrames: 3,
      endHangFrames: 3,
      minActivityMs: 120,
      noiseFloorAdapt: 0.05,
      smoothing: 1
    });
    const loud = new Float32Array(480).fill(1);
    const quiet = new Float32Array(480).fill(0);
    expect(vad.observe(loud).event).toBeNull();
    expect(vad.observe(loud).event).toBeNull();
    expect(vad.observe(loud).event?.type).toBe("started");
    for (let index = 0; index < 5; index += 1) {
      expect(vad.observe(loud).event).toBeNull();
    }

    expect(vad.observe(quiet).event).toBeNull();
    expect(vad.observe(quiet).event).toBeNull();
    expect(vad.observe(quiet).event?.type).toBe("ended");
  });
});
