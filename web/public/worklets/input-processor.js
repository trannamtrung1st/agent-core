class InputProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this._pending = new Float32Array(0);
    this._offset = 0;
    this._emit = false;
    this._resampler = new StreamingResampler(sampleRate, 24000);
    this.port.onmessage = (event) => {
      if (!event.data || typeof event.data !== "object") {
        return;
      }

      if (event.data.type === "emit") {
        this._emit = true;
        return;
      }

      if (event.data.type === "pause") {
        this._emit = false;
        return;
      }

      if (event.data.type === "reset") {
        this._pending = new Float32Array(0);
        this._offset = 0;
        this._resampler = new StreamingResampler(sampleRate, 24000);
        this._emit = false;
      }
    };
  }

  process(inputs) {
    const input = inputs[0];
    const channel = input && input.length > 0 ? input[0] : null;
    if (!channel || channel.length === 0 || !this._emit) {
      return true;
    }

    const resampled = this._resampler.process(channel);
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

class StreamingResampler {
  constructor(inputRate, outputRate) {
    this.step = inputRate / outputRate;
    this.leftover = new Float32Array(0);
    this.phase = 0;
    this.lowpass = 0;
  }

  process(input) {
    if (this.step === 1) {
      return input;
    }

    const merged = new Float32Array(this.leftover.length + input.length);
    merged.set(this.leftover);
    merged.set(input, this.leftover.length);
    const output = [];
    while (this.phase + 1 < merged.length) {
      const index = Math.floor(this.phase);
      const fraction = this.phase - index;
      const left = merged[index] || 0;
      const right = merged[index + 1] || left;
      const interpolated = left * (1 - fraction) + right * fraction;
      this.lowpass = this.lowpass * 0.2 + interpolated * 0.8;
      output.push(this.lowpass);
      this.phase += this.step;
    }

    const consumed = Math.min(merged.length, Math.floor(this.phase));
    this.leftover = merged.slice(consumed);
    this.phase -= consumed;
    const result = new Float32Array(output.length);
    result.set(output);
    return result;
  }
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
