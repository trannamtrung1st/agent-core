export const CANONICAL_RATE = 24000;
export const FRAME_SAMPLES = 480;

export class StreamingResampler {
  private readonly step: number;
  private leftover = new Float32Array(0);
  private phase = 0;
  private lowpass = 0;

  constructor(inputRate: number, outputRate: number = CANONICAL_RATE) {
    this.step = inputRate / outputRate;
  }

  process(input: Float32Array): Float32Array {
    if (this.step === 1) {
      return input;
    }

    const merged = new Float32Array(this.leftover.length + input.length);
    merged.set(this.leftover);
    merged.set(input, this.leftover.length);
    const output: number[] = [];
    while (this.phase + 1 < merged.length) {
      const index = Math.floor(this.phase);
      const fraction = this.phase - index;
      const left = merged[index] ?? 0;
      const right = merged[index + 1] ?? left;
      const interpolated = left * (1 - fraction) + right * fraction;
      this.lowpass = this.lowpass * 0.2 + interpolated * 0.8;
      output.push(this.lowpass);
      this.phase += this.step;
    }

    const consumed = Math.min(merged.length, Math.floor(this.phase));
    this.leftover = merged.slice(consumed);
    this.phase -= consumed;
    return Float32Array.from(output);
  }
}

export function resampleToCanonical(input: Float32Array, inputRate: number): Float32Array {
  return new StreamingResampler(inputRate).process(input);
}

export function encodePcm16Le(samples: Float32Array): Uint8Array {
  const bytes = new Uint8Array(samples.length * 2);
  const view = new DataView(bytes.buffer);
  for (let index = 0; index < samples.length; index += 1) {
    const clamped = Math.max(-1, Math.min(1, samples[index] ?? 0));
    const value = clamped < 0 ? Math.round(clamped * 0x8000) : Math.round(clamped * 0x7fff);
    view.setInt16(index * 2, value, true);
  }

  return bytes;
}

export function readInt16Le(bytes: Uint8Array, sampleIndex: number): number {
  return bytes[sampleIndex * 2]! | (bytes[sampleIndex * 2 + 1]! << 8);
}

export function decodePcm16Le(bytes: Uint8Array): Float32Array {
  const samples = new Float32Array(Math.floor(bytes.length / 2));
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  for (let index = 0; index < samples.length; index += 1) {
    samples[index] = view.getInt16(index * 2, true) / 0x8000;
  }

  return samples;
}

export class CanonicalFramer {
  private pending = new Float32Array(0);

  push(samples: Float32Array): Float32Array[] {
    const merged = new Float32Array(this.pending.length + samples.length);
    merged.set(this.pending);
    merged.set(samples, this.pending.length);
    const frames: Float32Array[] = [];
    let offset = 0;
    while (offset + FRAME_SAMPLES <= merged.length) {
      frames.push(merged.slice(offset, offset + FRAME_SAMPLES));
      offset += FRAME_SAMPLES;
    }

    this.pending = merged.slice(offset);
    return frames;
  }

  reset(): void {
    this.pending = new Float32Array(0);
  }
}
