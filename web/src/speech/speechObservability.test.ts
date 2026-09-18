import { describe, expect, it } from "vitest";
import {
  recordSpeechObservation,
  resetSpeechObservations,
  snapshotSpeechObservations,
  SPEECH_OBSERVATION_CAPACITY
} from "./speechObservability";

describe("speechObservability", () => {
  it("drops oldest observations when the ring buffer is full", () => {
    resetSpeechObservations();
    for (let i = 0; i < SPEECH_OBSERVATION_CAPACITY + 5; i += 1) {
      recordSpeechObservation({ name: "speech.partial.count", value: i });
    }
    const snapshot = snapshotSpeechObservations();
    expect(snapshot).toHaveLength(SPEECH_OBSERVATION_CAPACITY);
    expect(snapshot[0]?.value).toBe(5);
    expect(snapshot.at(-1)?.value).toBe(SPEECH_OBSERVATION_CAPACITY + 4);
    expect(JSON.stringify(snapshot)).not.toMatch(/api[_-]?key|sk-|pcm|transcript/i);
  });
});
