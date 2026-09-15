export const CANONICAL_RATE = 24000;
export const FRAME_SAMPLES = 480;

export function resampleToCanonical(input: Float32Array, inputRate: number): Float32Array {
  if (inputRate === CANONICAL_RATE) {
    return input;
  }

  const ratio = inputRate / CANONICAL_RATE;
  const outLength = Math.max(0, Math.floor(input.length / ratio));
  const output = new Float32Array(outLength);
  for (let index = 0; index < outLength; index += 1) {
    const source = index * ratio;
    const left = Math.floor(source);
    const right = Math.min(left + 1, input.length - 1);
    const fraction = source - left;
    output[index] = input[left] * (1 - fraction) + input[right] * fraction;
  }

  return output;
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
