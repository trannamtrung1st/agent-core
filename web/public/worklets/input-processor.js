class InputProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this._pending = new Float32Array(0);
    this._offset = 0;
    this._emit = false;
    this.port.onmessage = (event) => {
      this._emit = event.data && event.data.type === "emit";
    };
  }

  process(inputs) {
    const input = inputs[0];
    const channel = input && input.length > 0 ? input[0] : null;
    if (!channel || channel.length === 0 || !this._emit) {
      return true;
    }

    const resampled = resample(channel, sampleRate, 24000);
    const merged = new Float32Array(this._pending.length + resampled.length);
    merged.set(this._pending);
    merged.set(resampled, this._pending.length);
    let cursor = 0;
    while (cursor + 480 <= merged.length) {
      const frame = merged.subarray(cursor, cursor + 480);
      const bytes = encode(frame);
      this.port.postMessage({ pcm: bytes.buffer, sampleOffset: this._offset }, [bytes.buffer]);
      this._offset += 480;
      cursor += 480;
    }

    this._pending = merged.slice(cursor);
    return true;
  }
}

function resample(input, inputRate, outputRate) {
  if (inputRate === outputRate) {
    return input;
  }

  const ratio = inputRate / outputRate;
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

function encode(samples) {
  const bytes = new Uint8Array(samples.length * 2);
  const view = new DataView(bytes.buffer);
  for (let index = 0; index < samples.length; index += 1) {
    const clamped = Math.max(-1, Math.min(1, samples[index] || 0));
    const value = clamped < 0 ? Math.round(clamped * 0x8000) : Math.round(clamped * 0x7fff);
    view.setInt16(index * 2, value, true);
  }

  return bytes;
}

registerProcessor("input-processor", InputProcessor);
