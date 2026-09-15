export const EARLY_AUDIO_MS = 60;
export const MAX_QUEUED_SAMPLES = 24000 * 2;

export type OutputAudioFrame = {
  sessionId?: string;
  attachmentId?: string;
  responseId?: string;
  frameSequence?: number;
  sampleOffset?: number;
  data: Uint8Array;
  isFinal?: boolean;
};

export type OutputAdmitDecision = "play" | "buffer" | "reject";

export class OutputAudioGate {
  private readonly started = new Set<string>();
  private readonly lastSequence = new Map<string, number>();
  private readonly lastSampleEnd = new Map<string, number>();
  private bufferedSamples = 0;

  markStarted(responseId: string): void {
    this.started.add(responseId);
  }

  hasStarted(responseId: string): boolean {
    return this.started.has(responseId);
  }

  queuedBuffered(): number {
    return this.bufferedSamples;
  }

  drop(responseId: string): void {
    this.started.delete(responseId);
    this.lastSequence.delete(responseId);
    this.lastSampleEnd.delete(responseId);
    this.bufferedSamples = 0;
  }

  reset(): void {
    this.started.clear();
    this.lastSequence.clear();
    this.lastSampleEnd.clear();
    this.bufferedSamples = 0;
  }

  admit(
    frame: OutputAudioFrame,
    expected: {
      sessionId: string | null;
      attachmentId: string | null;
      tombstones: Record<string, string>;
      stopped: Set<string>;
      queuedSamples: number;
    }
  ): OutputAdmitDecision {
    const responseId = frame.responseId;
    if (!responseId || !expected.sessionId || !expected.attachmentId) {
      return "reject";
    }

    if (frame.sessionId !== expected.sessionId || frame.attachmentId !== expected.attachmentId) {
      return "reject";
    }

    if (expected.tombstones[responseId] || expected.stopped.has(responseId)) {
      return "reject";
    }

    const samples = Math.floor(frame.data.length / 2);
    if (frame.data.length % 2 !== 0) {
      return "reject";
    }

    if (typeof frame.frameSequence !== "number" || frame.frameSequence < 1 || typeof frame.sampleOffset !== "number" || frame.sampleOffset < 0) {
      return "reject";
    }

    const previousSequence = this.lastSequence.get(responseId);
    const previousEnd = this.lastSampleEnd.get(responseId) ?? 0;
    if (previousSequence !== undefined) {
      if (frame.frameSequence !== previousSequence + 1 || frame.sampleOffset !== previousEnd) {
        return "reject";
      }
    } else if (frame.sampleOffset !== 0) {
      return "reject";
    }

    if (expected.queuedSamples + this.bufferedSamples + samples > MAX_QUEUED_SAMPLES) {
      return "reject";
    }

    this.lastSequence.set(responseId, frame.frameSequence);
    this.lastSampleEnd.set(responseId, frame.sampleOffset + samples);
    if (!this.started.has(responseId)) {
      this.bufferedSamples += samples;
      return "buffer";
    }

    return "play";
  }

  commitBuffered(frame: OutputAudioFrame, queuedSamples: number): boolean {
    const responseId = frame.responseId;
    if (!responseId) {
      return false;
    }

    const samples = Math.floor(frame.data.length / 2);
    const remainingBuffered = Math.max(0, this.bufferedSamples - samples);
    if (queuedSamples + remainingBuffered + samples > MAX_QUEUED_SAMPLES) {
      return false;
    }

    this.bufferedSamples = remainingBuffered;
    return true;
  }
}
