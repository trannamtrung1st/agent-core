class OutputProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this._canonical = new Float32Array(0);
    this._device = new Float32Array(0);
    this._resampler = new StreamingResampler(24000, sampleRate);
    this._consumed = 0;
    this._underrun = 0;
    this._responseId = null;
    this._epoch = 0;
    this._final = false;
    this._closed = true;
    this._gain = 1;
    this._targetGain = 1;
    this._gainStart = 1;
    this._rampLeft = 0;
    this._rampTotal = 1;
    this._rendered = {};
    this._maxQueued = 24000 * 2;
    this._flushRequests = [];
    this._deviceRendered = 0;
    this.port.onmessage = (event) => this.onMessage(event.data);
  }

  onMessage(data) {
    if (!data || typeof data !== "object") {
      return;
    }

    if (data.type === "enqueue") {
      const incomingId = data.responseId || this._responseId;
      if (this._responseId && incomingId && incomingId !== this._responseId && !this._closed) {
        return;
      }

      if (!this._responseId || incomingId !== this._responseId) {
        this.beginResponse(incomingId);
      }

      if (data.pcm) {
        const incoming = data.pcm instanceof Float32Array ? data.pcm : new Float32Array(data.pcm);
        if (incoming.length > 0) {
          if (this._canonical.length + incoming.length > this._maxQueued) {
            this.port.postMessage({ type: "overflow", queued: this._canonical.length, epoch: this._epoch });
            this.emitSnapshot();
            return;
          }

          const merged = new Float32Array(this._canonical.length + incoming.length);
          merged.set(this._canonical);
          merged.set(incoming, this._canonical.length);
          this._canonical = merged;
        }
      }

      if (data.isFinal) {
        this._final = true;
        this.maybeComplete();
      }

      this.emitSnapshot();
      return;
    }

    if (data.type === "gain") {
      this._gainStart = this._gain;
      this._targetGain = typeof data.gain === "number" ? data.gain : 1;
      this._rampTotal = Math.max(1, Math.round(((data.rampMs ?? 20) / 1000) * sampleRate));
      this._rampLeft = this._rampTotal;
      return;
    }

    if (data.type === "flush") {
      this._flushRequests.push({
        responseId: data.responseId,
        epoch: typeof data.epoch === "number" ? data.epoch : this._epoch
      });
      return;
    }

    if (data.type === "snapshot") {
      this.emitSnapshot();
    }
  }

  beginResponse(responseId) {
    this._canonical = new Float32Array(0);
    this._device = new Float32Array(0);
    this._resampler = new StreamingResampler(24000, sampleRate);
    this._consumed = 0;
    this._deviceRendered = 0;
    this._underrun = 0;
    this._responseId = responseId || null;
    this._final = false;
    this._closed = false;
    if (responseId) {
      this._rendered[responseId] = this._rendered[responseId] || 0;
    }
  }

  emitSnapshot() {
    this.port.postMessage({
      type: "snapshot",
      consumed: this._consumed,
      queued: this._canonical.length,
      underrun: this._underrun,
      responseId: this._responseId,
      final: this._final,
      closed: this._closed,
      epoch: this._epoch,
      rendered: this._rendered
    });
  }

  maybeComplete() {
    if (
      this._closed
      || !this._final
      || this._canonical.length > 0
      || this._device.length > 0
      || this._resampler.pending() > 0
    ) {
      return;
    }

    const responseId = this._responseId;
    this._closed = true;
    this._responseId = null;
    this.port.postMessage({
      type: "complete",
      consumed: this._consumed,
      queued: 0,
      responseId,
      epoch: this._epoch
    });
    this.emitSnapshot();
  }

  creditRendered(frames) {
    if (frames <= 0) {
      return;
    }

    this._deviceRendered += frames;
    this._consumed = Math.floor((this._deviceRendered * 24000) / sampleRate);
    if (this._responseId) {
      this._rendered[this._responseId] = this._consumed;
    }
  }

  fillDevice(needed) {
    while (this._device.length < needed && this._canonical.length > 0) {
      const stillNeed = needed - this._device.length;
      const take = Math.min(
        this._canonical.length,
        Math.max(1, Math.ceil((stillNeed * 24000) / sampleRate))
      );
      const chunk = this._canonical.subarray(0, take);
      const converted = this._resampler.process(chunk);
      this._canonical = this._canonical.subarray(take);
      if (converted.length === 0) {
        continue;
      }

      const merged = new Float32Array(this._device.length + converted.length);
      merged.set(this._device);
      merged.set(converted, this._device.length);
      this._device = merged;
    }

    if (this._final && this._canonical.length === 0) {
      const flushed = this._resampler.process(new Float32Array(0), true);
      if (flushed.length > 0) {
        const merged = new Float32Array(this._device.length + flushed.length);
        merged.set(this._device);
        merged.set(flushed, this._device.length);
        this._device = merged;
      }
    }
  }

  process(_inputs, outputs) {
    const output = outputs[0];
    const channel = output && output.length > 0 ? output[0] : null;
    if (!channel) {
      return true;
    }

    const needed = channel.length;
    const applyGain = (value) => {
      if (this._rampLeft > 0) {
        const done = this._rampTotal - this._rampLeft + 1;
        this._gain = this._gainStart + (this._targetGain - this._gainStart) * (done / this._rampTotal);
        this._rampLeft -= 1;
      } else {
        this._gain = this._targetGain;
      }
      return value * this._gain;
    };

    this.fillDevice(needed);
    if (this._device.length >= needed) {
      for (let index = 0; index < needed; index += 1) {
        channel[index] = applyGain(this._device[index]);
      }
      this._device = this._device.subarray(needed);
      this.creditRendered(needed);
      this.emitSnapshot();
      this.maybeComplete();
    } else if (this._final && (this._device.length > 0 || this._canonical.length > 0)) {
      this.fillDevice(needed);
      const remaining = Math.min(this._device.length, needed);
      for (let index = 0; index < remaining; index += 1) {
        channel[index] = applyGain(this._device[index]);
      }
      channel.fill(0, remaining);
      this.creditRendered(remaining);
      this._device = new Float32Array(0);
      this._canonical = new Float32Array(0);
      this.emitSnapshot();
      this.maybeComplete();
    } else if (this._final) {
      channel.fill(0);
      this.maybeComplete();
    } else {
      channel.fill(0);
      this._underrun += needed;
    }

    this.acknowledgeFlush();
    return true;
  }

  acknowledgeFlush() {
    while (this._flushRequests.length > 0) {
      const request = this._flushRequests[0];
      const matchesResponse = !request.responseId || request.responseId === this._responseId;
      const matchesEpoch = request.epoch === this._epoch;
      if (!matchesResponse || !matchesEpoch) {
        this._flushRequests.shift();
        this.port.postMessage({
          type: "flushed",
          consumed: this._consumed,
          epoch: request.epoch,
          responseId: request.responseId
        });
        continue;
      }

      const stoppedAt = this._consumed;
      this._canonical = new Float32Array(0);
      this._device = new Float32Array(0);
      this._resampler = new StreamingResampler(24000, sampleRate);
      this._responseId = null;
      this._consumed = 0;
      this._deviceRendered = 0;
      this._final = false;
      this._closed = true;
      this._epoch += 1;
      this._flushRequests.shift();
      this.port.postMessage({
        type: "flushed",
        consumed: stoppedAt,
        epoch: request.epoch,
        responseId: request.responseId
      });
      this.emitSnapshot();
      break;
    }
  }
}

class StreamingResampler {
  constructor(inputRate, outputRate) {
    this.step = inputRate / outputRate;
    this.leftover = new Float32Array(0);
    this.phase = 0;
    this.lowpass = 0;
  }

  pending() {
    return this.leftover.length;
  }

  process(input, flush = false) {
    if (this.step === 1) {
      const copy = new Float32Array(input.length);
      copy.set(input);
      return copy;
    }

    const merged = new Float32Array(this.leftover.length + input.length);
    merged.set(this.leftover);
    merged.set(input, this.leftover.length);
    const source = flush && merged.length > 0
      ? (() => {
          const padded = new Float32Array(merged.length + 1);
          padded.set(merged);
          padded[merged.length] = merged[merged.length - 1];
          return padded;
        })()
      : merged;
    const output = [];
    while (this.phase + 1 < source.length) {
      const index = Math.floor(this.phase);
      const fraction = this.phase - index;
      const left = source[index] || 0;
      const right = source[index + 1] || left;
      const interpolated = left * (1 - fraction) + right * fraction;
      this.lowpass = this.lowpass * 0.2 + interpolated * 0.8;
      output.push(this.lowpass);
      this.phase += this.step;
    }

    if (flush) {
      this.leftover = new Float32Array(0);
      this.phase = 0;
    } else {
      const consumed = Math.min(merged.length, Math.floor(this.phase));
      this.leftover = merged.slice(consumed);
      this.phase -= consumed;
    }

    const result = new Float32Array(output.length);
    result.set(output);
    return result;
  }
}

registerProcessor("output-processor", OutputProcessor);
globalThis.AgentCoreOutputProcessor = OutputProcessor;
