export const MAX_QUEUED_SAMPLES = 24000 * 2;

export class PlaybackQueue {
  private queued = new Float32Array(0);
  consumed = 0;
  underrunSamples = 0;

  get queuedSamples(): number {
    return this.queued.length;
  }

  enqueue(samples: Float32Array): "ok" | "overflow" {
    if (this.queued.length + samples.length > MAX_QUEUED_SAMPLES) {
      return "overflow";
    }

    const merged = new Float32Array(this.queued.length + samples.length);
    merged.set(this.queued);
    merged.set(samples, this.queued.length);
    this.queued = merged;
    return "ok";
  }

  consume(frames: number): Float32Array {
    const rendered = new Float32Array(frames);
    if (this.queued.length >= frames) {
      rendered.set(this.queued.subarray(0, frames));
      this.queued = this.queued.subarray(frames);
      this.consumed += frames;
    } else {
      rendered.fill(0);
      this.underrunSamples += frames;
    }

    return rendered;
  }

  flush(): void {
    this.queued = new Float32Array(0);
  }
}
