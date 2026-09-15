export type VadConfig = {
  startThreshold: number;
  endThreshold: number;
  startHangFrames: number;
  endHangFrames: number;
  minActivityMs: number;
  noiseFloorAdapt: number;
  smoothing: number;
};

export const defaultVad: VadConfig = {
  startThreshold: 0.7,
  endThreshold: 0.4,
  startHangFrames: 3,
  endHangFrames: 15,
  minActivityMs: 120,
  noiseFloorAdapt: 0.05,
  smoothing: 0.3
};

export type VadEvent = { type: "started" | "ended"; activityScore: number };

export class VoiceActivityObserver {
  private inSpeech = false;
  private startHang = 0;
  private endHang = 0;
  private speechFrames = 0;
  private noiseFloor = 0.01;
  private smoothed = 0;

  constructor(private readonly config: VadConfig = defaultVad) {}

  reset(): void {
    this.inSpeech = false;
    this.startHang = 0;
    this.endHang = 0;
    this.speechFrames = 0;
    this.noiseFloor = 0.01;
    this.smoothed = 0;
  }

  observe(frame: Float32Array): { activityScore: number; event: VadEvent | null } {
    let sum = 0;
    for (let index = 0; index < frame.length; index += 1) {
      const sample = frame[index] ?? 0;
      sum += sample * sample;
    }

    const energy = Math.sqrt(sum / Math.max(1, frame.length));
    if (!this.inSpeech) {
      this.noiseFloor += this.config.noiseFloorAdapt * (energy - this.noiseFloor);
    }

    const speechRef = Math.max(this.noiseFloor * 4, 0.02);
    const raw = Math.max(0, Math.min(1, (energy - this.noiseFloor) / Math.max(1e-6, speechRef - this.noiseFloor)));
    this.smoothed += this.config.smoothing * (raw - this.smoothed);
    const activityScore = this.smoothed;
    let event: VadEvent | null = null;

    if (!this.inSpeech) {
      if (activityScore > this.config.startThreshold) {
        this.startHang += 1;
      } else {
        this.startHang = 0;
      }

      if (this.startHang >= this.config.startHangFrames) {
        this.inSpeech = true;
        this.endHang = 0;
        this.speechFrames = this.startHang;
        this.startHang = 0;
        event = { type: "started", activityScore };
      }
    } else {
      this.speechFrames += 1;
      if (activityScore < this.config.endThreshold) {
        this.endHang += 1;
      } else {
        this.endHang = 0;
      }

      const durationMs = this.speechFrames * 20;
      if (this.endHang >= this.config.endHangFrames) {
        this.inSpeech = false;
        this.endHang = 0;
        if (durationMs >= this.config.minActivityMs) {
          event = { type: "ended", activityScore };
        }
      }
    }

    return { activityScore, event };
  }
}
