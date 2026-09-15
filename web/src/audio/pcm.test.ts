import { describe, expect, it } from "vitest";
import { encodePcm16Le, readInt16Le, resampleToCanonical, StreamingResampler } from "./pcm";

describe("pcm", () => {
  it("resamples 48 kHz to 24 kHz and preserves length ratio", () => {
    const input = new Float32Array(480);
    for (let index = 0; index < input.length; index += 1) {
      input[index] = Math.sin((index / input.length) * Math.PI);
    }

    const output = resampleToCanonical(input, 48000);
    expect(output.length).toBeGreaterThanOrEqual(239);
    expect(output.length).toBeLessThanOrEqual(240);
  });

  it("keeps 44.1 kHz long-run output count without cumulative drift", () => {
    const resampler = new StreamingResampler(44100);
    const quantum = 128;
    const blocks = 1000;
    let produced = 0;
    let previous = 0;
    for (let block = 0; block < blocks; block += 1) {
      const input = new Float32Array(quantum);
      for (let index = 0; index < quantum; index += 1) {
        input[index] = Math.sin((2 * Math.PI * 1000 * (block * quantum + index)) / 44100);
      }
      const output = resampler.process(input);
      if (output.length > 0) {
        expect(Math.abs(output[0]! - previous)).toBeLessThan(0.5);
        previous = output[output.length - 1]!;
      }
      produced += output.length;
    }

    const expected = (blocks * quantum * 24000) / 44100;
    expect(Math.abs(produced - expected)).toBeLessThan(2);
  });

  it("attenuates 12 kHz content when downsampling 44.1 kHz", () => {
    const low = energyAfter(1000);
    const high = energyAfter(12000);
    expect(high).toBeLessThan(low * 0.8);
  });

  it("upsamples 24 kHz to 48 kHz and 44.1 kHz without dropping continuity across chunks", () => {
    for (const rate of [48000, 44100]) {
      const resampler = new StreamingResampler(24000, rate);
      let produced = 0;
      for (let block = 0; block < 20; block += 1) {
        const input = new Float32Array(480);
        for (let index = 0; index < input.length; index += 1) {
          input[index] = Math.sin((2 * Math.PI * 440 * (block * 480 + index)) / 24000);
        }
        produced += resampler.process(input).length;
      }

      const expected = (20 * 480 * rate) / 24000;
      expect(Math.abs(produced - expected)).toBeLessThan(4);
    }
  });

  it("encodes PCM16 little endian", () => {
    const encoded = encodePcm16Le(new Float32Array([0.5, -1]));
    expect(encoded.length).toBe(4);
    expect(readInt16Le(encoded, 0) & 0xff).toBe(encoded[0]);
    expect(encoded[0]).not.toBe(encoded[1]);
    const view = new DataView(encoded.buffer);
    expect(view.getInt16(0, true)).toBe(Math.round(0.5 * 0x7fff));
    expect(view.getInt16(2, true)).toBe(-0x8000);
  });
});

function energyAfter(hertz: number): number {
  const resampler = new StreamingResampler(44100);
  const input = new Float32Array(44100);
  for (let index = 0; index < input.length; index += 1) {
    input[index] = Math.sin((2 * Math.PI * hertz * index) / 44100);
  }
  const output = resampler.process(input);
  let energy = 0;
  for (let index = 0; index < output.length; index += 1) {
    energy += output[index]! * output[index]!;
  }
  return energy / output.length;
}
