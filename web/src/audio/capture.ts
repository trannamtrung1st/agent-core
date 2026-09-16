import { decodePcm16Le } from "./pcm";
import { MAX_QUEUED_SAMPLES } from "./outputAdmission";
import { VoiceActivityObserver } from "./vad";

export type CaptureHooks = {
  sendAudio: (frame: { frameSequence: number; sampleOffset: number; data: Uint8Array }) => Promise<void> | void;
  speechStarted: (utteranceId: string, sampleOffset: number, activityScore: number) => Promise<void> | void;
  speechEnded: (
    utteranceId: string,
    sampleOffset: number,
    activityScore: number,
    closing?: boolean
  ) => Promise<void> | void;
};

type Prepared = {
  context: AudioContext;
  source: MediaStreamAudioSourceNode | null;
  worklet: AudioWorkletNode;
  output: AudioWorkletNode;
  gain: GainNode;
  stream: MediaStream;
};

class MicrophoneCapture {
  private prepared: Prepared | null = null;
  private streaming = false;
  private workletReady = false;
  private outputReady = false;
  private consumedSamples = 0;
  private queuedSamples = 0;
  private outputResponseId: string | null = null;
  private outputEpoch = 0;
  private outputClosed = true;
  private renderedByResponse: Record<string, number> = {};
  private completedResponses: string[] = [];
  private onConsumed: ((consumed: number) => void) | null = null;
  private onPlaybackComplete: ((responseId: string, consumed: number) => void) | null = null;
  private onOverflow: (() => void) | null = null;
  private frameSequence = 1;
  private sampleOffset = 0;
  private streamGeneration = 0;
  private transmittedByGeneration = new Map<number, number>();
  private utteranceId: string | null = null;
  private readonly vad = new VoiceActivityObserver();
  private hooks: CaptureHooks | null = null;
  private outbound: Promise<void> = Promise.resolve();
  private startChain: Promise<void> = Promise.resolve();
  outgoingHold: (() => Promise<void>) | null = null;

  isPrepared(): boolean {
    return this.prepared !== null;
  }

  isStreaming(): boolean {
    return this.streaming;
  }

  workletLoaded(): boolean {
    return this.workletReady && this.outputReady;
  }

  outputWorkletLoaded(): boolean {
    return this.outputReady;
  }

  playbackConsumed(): number {
    return this.consumedSamples;
  }

  playbackQueued(): number {
    return this.queuedSamples;
  }

  playbackResponseId(): string | null {
    return this.outputResponseId;
  }

  playbackEpoch(): number {
    return this.outputEpoch;
  }

  playbackClosed(): boolean {
    return this.outputClosed;
  }

  playbackRendered(): Record<string, number> {
    return { ...this.renderedByResponse };
  }

  playbackCompletedResponses(): string[] {
    return [...this.completedResponses];
  }

  setPlaybackListener(listener: ((consumed: number) => void) | null): void {
    this.onConsumed = listener;
  }

  setPlaybackCompleteListener(listener: ((responseId: string, consumed: number) => void) | null): void {
    this.onPlaybackComplete = listener;
  }

  setOverflowListener(listener: (() => void) | null): void {
    this.onOverflow = listener;
  }

  async preflight(): Promise<void> {
    this.release();
    if (typeof AudioWorkletNode === "undefined") {
      throw new Error("AudioWorklet is not available.");
    }

    const stream = await Promise.race([
      navigator.mediaDevices.getUserMedia({
        audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true },
        video: false
      }),
      new Promise<MediaStream>((_, reject) => {
        window.setTimeout(() => reject(new Error("getUserMedia timed out.")), 4000);
      })
    ]);
    const context = new AudioContext();
    if (context.state === "suspended") {
      await Promise.race([
        context.resume(),
        new Promise((_, reject) => {
          window.setTimeout(() => reject(new Error("AudioContext resume timed out.")), 4000);
        })
      ]);
    }

    if (context.state !== "running") {
      stream.getTracks().forEach((track) => track.stop());
      await context.close();
      throw new Error("AudioContext could not resume.");
    }

    try {
      await Promise.race([
        Promise.all([
          context.audioWorklet.addModule("/worklets/input-processor.js"),
          context.audioWorklet.addModule("/worklets/output-processor.js")
        ]),
        new Promise((_, reject) => {
          window.setTimeout(() => reject(new Error("AudioWorklet addModule timed out.")), 4000);
        })
      ]);
      const worklet = new AudioWorkletNode(context, "input-processor", {
        numberOfInputs: 1,
        numberOfOutputs: 1,
        outputChannelCount: [1]
      });
      const output = new AudioWorkletNode(context, "output-processor", {
        numberOfInputs: 1,
        numberOfOutputs: 1,
        outputChannelCount: [1]
      });
      const gain = context.createGain();
      gain.gain.value = 0;
      worklet.connect(gain);
      gain.connect(context.destination);
      output.connect(context.destination);
      worklet.port.onmessage = (event: MessageEvent<{ pcm: ArrayBuffer; sampleOffset: number }>) => {
        return this.onFrame(event.data.pcm, event.data.sampleOffset);
      };
      output.port.onmessage = (event: MessageEvent<{
        type?: string;
        consumed?: number;
        queued?: number;
        responseId?: string | null;
        epoch?: number;
        closed?: boolean;
        rendered?: Record<string, number>;
      }>) => {
        if (event.data.type === "flushed") {
          const stoppedAt = typeof event.data.consumed === "number" ? event.data.consumed : 0;
          this.consumedSamples = stoppedAt;
          const responseId = typeof event.data.responseId === "string" ? event.data.responseId : "";
          const epoch = typeof event.data.epoch === "number" ? event.data.epoch : this.outputEpoch;
          const waiters = this.flushWaiters.filter((waiter) => waiter.responseId === responseId && waiter.epoch === epoch);
          this.flushWaiters = this.flushWaiters.filter((waiter) => waiter.responseId !== responseId || waiter.epoch !== epoch);
          waiters.forEach((waiter) => waiter.resolve(stoppedAt));
        }
        if (event.data.type === "overflow") {
          this.queuedSamples = typeof event.data.queued === "number" ? event.data.queued : this.queuedSamples;
          this.onOverflow?.();
        }
        if (typeof event.data.queued === "number") {
          this.queuedSamples = event.data.queued;
        }
        if (event.data.type === "complete" && event.data.responseId) {
          this.completedResponses.push(event.data.responseId);
          const consumed = typeof event.data.consumed === "number" ? event.data.consumed : this.consumedSamples;
          this.consumedSamples = consumed;
          this.onPlaybackComplete?.(event.data.responseId, consumed);
        }
        if (typeof event.data.epoch === "number") {
          this.outputEpoch = event.data.epoch;
        }
        if (typeof event.data.closed === "boolean") {
          this.outputClosed = event.data.closed;
        }
        if (event.data.rendered) {
          this.renderedByResponse = event.data.rendered;
        }
        if ("responseId" in event.data) {
          this.outputResponseId = event.data.responseId ?? null;
        }
        if (typeof event.data.consumed === "number") {
          this.consumedSamples = event.data.consumed;
          this.onConsumed?.(event.data.consumed);
        }
      };
      this.prepared = { context, source: null, worklet, output, gain, stream };
      this.workletReady = true;
      this.outputReady = true;
    } catch (error) {
      stream.getTracks().forEach((track) => track.stop());
      await context.close();
      throw error instanceof Error ? error : new Error("AudioWorklet failed to load.");
    }
  }

  async muteInput(): Promise<void> {
    const run = async () => {
      const mutedGeneration = this.streamGeneration;
      this.streamGeneration += 1;
      this.streaming = false;
      this.prepared?.worklet.port.postMessage({ type: "pause" });
      this.prepared?.worklet.port.postMessage({ type: "reset" });
      this.frameSequence = 1;
      this.sampleOffset = 0;
      this.vad.reset();
      await this.closeOpenUtterance(mutedGeneration);
    };

    const pending = this.startChain.then(run, run);
    this.startChain = pending.catch(() => undefined);
    return pending;
  }

  async start(hooks: CaptureHooks): Promise<void> {
    const run = async () => {
      if (!this.prepared) {
        throw new Error("Capture is not prepared.");
      }

      const rotatedGeneration = this.streamGeneration;
      this.streamGeneration += 1;
      this.streaming = false;
      await this.closeOpenUtterance(rotatedGeneration);
      this.hooks = hooks;
      this.streaming = true;
      this.frameSequence = 1;
      this.sampleOffset = 0;
      this.transmittedByGeneration.clear();
      this.vad.reset();
      if (this.prepared.source === null) {
        this.prepared.source = this.prepared.context.createMediaStreamSource(this.prepared.stream);
        this.prepared.source.connect(this.prepared.worklet);
      }

      this.prepared.worklet.port.postMessage({ type: "reset" });
      this.prepared.worklet.port.postMessage({ type: "emit" });
    };

    const pending = this.startChain.then(run, run);
    this.startChain = pending.catch(() => undefined);
    return pending;
  }

  private async closeOpenUtterance(generation: number): Promise<void> {
    const utteranceId = this.utteranceId;
    const hooks = this.hooks;
    this.utteranceId = null;
    await this.outbound.catch(() => undefined);
    const endedOffset = this.transmittedByGeneration.get(generation) ?? 0;
    this.transmittedByGeneration.delete(generation);
    if (utteranceId && hooks) {
      await hooks.speechEnded(utteranceId, endedOffset, 0, true);
    }
  }

  private flushWaiters: Array<{ responseId: string; epoch: number; resolve: (consumed: number) => void }> = [];

  setGain(gain: number, rampMs = 20): void {
    this.prepared?.output.port.postMessage({ type: "gain", gain, rampMs });
  }

  enqueuePlayback(responseId: string, pcm: Uint8Array, isFinal = false): boolean {
    const samples = decodePcm16Le(pcm);
    if (this.queuedSamples + samples.length > MAX_QUEUED_SAMPLES) {
      return false;
    }

    this.queuedSamples += samples.length;
    this.prepared?.output.port.postMessage(
      { type: "enqueue", responseId, pcm: samples.buffer, isFinal },
      [samples.buffer]
    );
    return true;
  }

  flushPlayback(responseId: string): Promise<number> {
    if (!this.prepared) {
      const stoppedAt = this.consumedSamples;
      this.consumedSamples = 0;
      this.queuedSamples = 0;
      this.outputResponseId = null;
      this.outputClosed = true;
      return Promise.resolve(stoppedAt);
    }

    const epoch = this.outputEpoch;
    const waiter = new Promise<number>((resolve) => {
      this.flushWaiters.push({ responseId, epoch, resolve });
    });
    this.prepared.output.port.postMessage({ type: "flush", responseId, epoch });
    return waiter.then((stoppedAt) => {
      this.consumedSamples = stoppedAt;
      this.queuedSamples = 0;
      this.outputResponseId = null;
      this.outputClosed = true;
      return stoppedAt;
    });
  }

  release(): void {
    this.streaming = false;
    this.hooks = null;
    this.workletReady = false;
    this.outputReady = false;
    const stoppedAt = this.consumedSamples;
    this.consumedSamples = 0;
    this.queuedSamples = 0;
    this.outputResponseId = null;
    this.outputEpoch = 0;
    this.outputClosed = true;
    this.renderedByResponse = {};
    this.completedResponses = [];
    this.onConsumed = null;
    this.onPlaybackComplete = null;
    this.onOverflow = null;
    this.utteranceId = null;
    const waiters = this.flushWaiters;
    this.flushWaiters = [];
    waiters.forEach((waiter) => waiter.resolve(stoppedAt));
    const prepared = this.prepared;
    this.prepared = null;
    if (!prepared) {
      return;
    }

    try {
      prepared.worklet.port.onmessage = null;
      prepared.output.port.onmessage = null;
      prepared.source?.disconnect();
      prepared.worklet.disconnect();
      prepared.output.disconnect();
      prepared.gain.disconnect();
    } catch {
      // ignored
    }

    prepared.stream.getTracks().forEach((track) => track.stop());
    void prepared.context.close();
  }

  private async onFrame(pcm: ArrayBuffer, workletOffset: number): Promise<void> {
    const generation = this.streamGeneration;
    if (!this.streaming || !this.hooks) {
      return;
    }

    void workletOffset;
    const bytes = new Uint8Array(pcm.slice(0));
    const floats = new Float32Array(bytes.length / 2);
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    for (let index = 0; index < floats.length; index += 1) {
      floats[index] = view.getInt16(index * 2, true) / 0x8000;
    }

    const activity = this.vad.observe(floats);
    const startOffset = this.sampleOffset;
    const sequence = this.frameSequence;
    this.frameSequence += 1;
    this.sampleOffset += floats.length;
    const endOffset = this.sampleOffset;
    if (activity.event?.type === "started") {
      this.utteranceId = crypto.randomUUID();
      await this.enqueueOutbound(generation, () => {
        if (this.streamGeneration !== generation || !this.streaming || !this.hooks) {
          return;
        }

        return this.hooks.speechStarted(this.utteranceId!, startOffset, activity.activityScore);
      });
    }

    if (this.outgoingHold) {
      await this.outgoingHold();
    }

    await this.enqueueOutbound(generation, async () => {
      if (this.streamGeneration !== generation || !this.streaming || !this.hooks) {
        return;
      }

      await this.hooks.sendAudio({
        frameSequence: sequence,
        sampleOffset: startOffset,
        data: bytes
      });
      if (this.streamGeneration !== generation || !this.streaming) {
        return;
      }

      this.transmittedByGeneration.set(generation, endOffset);
    });

    if (activity.event?.type === "ended" && this.utteranceId) {
      const utteranceId = this.utteranceId;
      this.utteranceId = null;
      await this.enqueueOutbound(generation, () => {
        if (this.streamGeneration !== generation || !this.streaming || !this.hooks) {
          return;
        }

        return this.hooks.speechEnded(utteranceId, endOffset, activity.activityScore);
      });
    }
  }

  private enqueueOutbound(generation: number, work: () => Promise<void> | void): Promise<void> {
    const next = this.outbound.then(async () => {
      if (this.streamGeneration !== generation || !this.streaming || !this.hooks) {
        return;
      }

      await work();
    });
    this.outbound = next.catch(() => undefined);
    return next;
  }
}

export const capture = new MicrophoneCapture();
