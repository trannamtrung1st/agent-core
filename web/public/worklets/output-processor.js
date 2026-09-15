class OutputProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this._queue = new Float32Array(0);
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
          const merged = new Float32Array(this._queue.length + incoming.length);
          merged.set(this._queue);
          merged.set(incoming, this._queue.length);
          this._queue = merged;
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
      if (!data.responseId || data.responseId === this._responseId) {
        this._queue = new Float32Array(0);
        this._responseId = null;
        this._consumed = 0;
        this._final = false;
        this._closed = true;
        this._epoch += 1;
      }

      this.port.postMessage({ type: "flushed", consumed: this._consumed, epoch: this._epoch });
      this.emitSnapshot();
      return;
    }

    if (data.type === "snapshot") {
      this.emitSnapshot();
    }
  }

  beginResponse(responseId) {
    this._queue = new Float32Array(0);
    this._consumed = 0;
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
      queued: this._queue.length,
      underrun: this._underrun,
      responseId: this._responseId,
      final: this._final,
      closed: this._closed,
      epoch: this._epoch,
      rendered: this._rendered
    });
  }

  maybeComplete() {
    if (this._closed || !this._final || this._queue.length > 0) {
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

    if (this._queue.length >= needed) {
      for (let index = 0; index < needed; index += 1) {
        channel[index] = applyGain(this._queue[index]);
      }
      this._queue = this._queue.subarray(needed);
      this._consumed += needed;
      if (this._responseId) {
        this._rendered[this._responseId] = (this._rendered[this._responseId] || 0) + needed;
      }
      this.emitSnapshot();
      this.maybeComplete();
    } else if (this._final && this._queue.length > 0) {
      const remaining = this._queue.length;
      for (let index = 0; index < remaining; index += 1) {
        channel[index] = applyGain(this._queue[index]);
      }
      channel.fill(0, remaining);
      this._queue = new Float32Array(0);
      this._consumed += remaining;
      if (this._responseId) {
        this._rendered[this._responseId] = (this._rendered[this._responseId] || 0) + remaining;
      }
      this.emitSnapshot();
      this.maybeComplete();
    } else if (this._final) {
      channel.fill(0);
      this.maybeComplete();
    } else {
      channel.fill(0);
      this._underrun += needed;
    }

    return true;
  }
}

registerProcessor("output-processor", OutputProcessor);
globalThis.AgentCoreOutputProcessor = OutputProcessor;
