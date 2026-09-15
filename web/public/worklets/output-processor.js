class OutputProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this._queue = new Float32Array(0);
    this._consumed = 0;
    this._underrun = 0;
    this._responseId = null;
    this._epoch = 0;
    this.port.onmessage = (event) => this.onMessage(event.data);
  }

  onMessage(data) {
    if (!data || typeof data !== "object") {
      return;
    }

    if (data.type === "enqueue" && data.pcm) {
      if (this._responseId && data.responseId && data.responseId !== this._responseId) {
        return;
      }

      this._responseId = data.responseId || this._responseId;
      const incoming = new Float32Array(data.pcm);
      const merged = new Float32Array(this._queue.length + incoming.length);
      merged.set(this._queue);
      merged.set(incoming, this._queue.length);
      this._queue = merged;
      return;
    }

    if (data.type === "flush") {
      if (!data.responseId || data.responseId === this._responseId) {
        this._queue = new Float32Array(0);
        this._responseId = null;
        this._epoch += 1;
      }

      this.port.postMessage({ type: "flushed", consumed: this._consumed, epoch: this._epoch });
      return;
    }

    if (data.type === "snapshot") {
      this.port.postMessage({
        type: "snapshot",
        consumed: this._consumed,
        queued: this._queue.length,
        underrun: this._underrun,
        responseId: this._responseId
      });
    }
  }

  process(_inputs, outputs) {
    const output = outputs[0];
    const channel = output && output.length > 0 ? output[0] : null;
    if (!channel) {
      return true;
    }

    const needed = channel.length;
    if (this._queue.length >= needed) {
      channel.set(this._queue.subarray(0, needed));
      this._queue = this._queue.subarray(needed);
      this._consumed += needed;
      this.port.postMessage({ type: "snapshot", consumed: this._consumed, queued: this._queue.length, underrun: this._underrun, responseId: this._responseId });
    } else {
      channel.fill(0);
      this._underrun += needed;
    }

    return true;
  }
}

registerProcessor("output-processor", OutputProcessor);
