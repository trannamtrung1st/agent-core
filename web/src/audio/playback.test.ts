import { describe, expect, it } from "vitest";
import { decodePcm16Le, encodePcm16Le } from "./pcm";
import { PlaybackQueue } from "./playback";

describe("playback queue", () => {
  it("does not advance consumed samples on underrun silence", () => {
    const queue = new PlaybackQueue();
    const rendered = queue.consume(480);
    expect(rendered.every((sample) => sample === 0)).toBe(true);
    expect(queue.consumed).toBe(0);
    expect(queue.underrunSamples).toBe(480);
  });

  it("advances consumed only for queued PCM and rejects overflow", () => {
    const queue = new PlaybackQueue();
    const pcm = encodePcm16Le(new Float32Array(480).fill(0.2));
    expect(queue.enqueue(decodePcm16Le(pcm))).toBe("ok");
    queue.consume(480);
    expect(queue.consumed).toBe(480);
    const huge = new Float32Array(24000 * 2 + 1);
    expect(queue.enqueue(huge)).toBe("overflow");
  });
});
