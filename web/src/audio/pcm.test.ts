import { describe, expect, it } from "vitest";
import { encodePcm16Le, readInt16Le, resampleToCanonical } from "./pcm";

describe("pcm", () => {
  it("resamples 48 kHz to 24 kHz and preserves length ratio", () => {
    const input = new Float32Array(480);
    for (let index = 0; index < input.length; index += 1) {
      input[index] = Math.sin((index / input.length) * Math.PI);
    }

    const output = resampleToCanonical(input, 48000);
    expect(output.length).toBe(240);
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
