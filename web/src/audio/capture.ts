import { VoiceActivityObserver } from "./vad";

export type CaptureHooks = {
  sendAudio: (frame: { frameSequence: number; sampleOffset: number; data: Uint8Array }) => Promise<void> | void;
  speechStarted: (utteranceId: string, sampleOffset: number, activityScore: number) => Promise<void> | void;
  speechEnded: (utteranceId: string, sampleOffset: number, activityScore: number) => Promise<void> | void;
};

type Prepared = {
  context: AudioContext;
  source: MediaStreamAudioSourceNode | null;
  worklet: AudioWorkletNode;
  gain: GainNode;
  stream: MediaStream;
};

class MicrophoneCapture {
  private prepared: Prepared | null = null;
  private streaming = false;
  private workletReady = false;
  private frameSequence = 1;
  private sampleOffset = 0;
  private utteranceId: string | null = null;
  private readonly vad = new VoiceActivityObserver();
  private hooks: CaptureHooks | null = null;

  isPrepared(): boolean {
    return this.prepared !== null;
  }

  isStreaming(): boolean {
    return this.streaming;
  }

  workletLoaded(): boolean {
    return this.workletReady;
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
        context.audioWorklet.addModule("/worklets/input-processor.js"),
        new Promise((_, reject) => {
          window.setTimeout(() => reject(new Error("AudioWorklet addModule timed out.")), 4000);
        })
      ]);
      const worklet = new AudioWorkletNode(context, "input-processor", {
        numberOfInputs: 1,
        numberOfOutputs: 1,
        outputChannelCount: [1]
      });
      const gain = context.createGain();
      gain.gain.value = 0;
      worklet.connect(gain);
      gain.connect(context.destination);
      worklet.port.onmessage = (event: MessageEvent<{ pcm: ArrayBuffer; sampleOffset: number }>) => {
        void this.onFrame(event.data.pcm, event.data.sampleOffset);
      };
      this.prepared = { context, source: null, worklet, gain, stream };
      this.workletReady = true;
    } catch (error) {
      stream.getTracks().forEach((track) => track.stop());
      await context.close();
      throw error instanceof Error ? error : new Error("AudioWorklet failed to load.");
    }
  }

  start(hooks: CaptureHooks): void {
    this.hooks = hooks;
    this.streaming = true;
    this.frameSequence = 1;
    this.sampleOffset = 0;
    this.vad.reset();
    if (this.prepared && this.prepared.source === null) {
      this.prepared.source = this.prepared.context.createMediaStreamSource(this.prepared.stream);
      this.prepared.source.connect(this.prepared.worklet);
    }

    this.prepared?.worklet.port.postMessage({ type: "emit" });
  }

  release(): void {
    this.streaming = false;
    this.hooks = null;
    this.workletReady = false;
    this.utteranceId = null;
    const prepared = this.prepared;
    this.prepared = null;
    if (!prepared) {
      return;
    }

    try {
      prepared.worklet.port.onmessage = null;
      prepared.source?.disconnect();
      prepared.worklet.disconnect();
      prepared.gain.disconnect();
    } catch {
      // ignored
    }

    prepared.stream.getTracks().forEach((track) => track.stop());
    void prepared.context.close();
  }

  private async onFrame(pcm: ArrayBuffer, workletOffset: number): Promise<void> {
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
    if (activity.event?.type === "started") {
      this.utteranceId = crypto.randomUUID();
      await this.hooks.speechStarted(this.utteranceId, this.sampleOffset, activity.activityScore);
    }

    await this.hooks.sendAudio({
      frameSequence: this.frameSequence,
      sampleOffset: this.sampleOffset,
      data: bytes
    });
    this.frameSequence += 1;
    this.sampleOffset += floats.length;

    if (activity.event?.type === "ended" && this.utteranceId) {
      await this.hooks.speechEnded(this.utteranceId, this.sampleOffset, activity.activityScore);
      this.utteranceId = null;
    }
  }
}

export const capture = new MicrophoneCapture();
