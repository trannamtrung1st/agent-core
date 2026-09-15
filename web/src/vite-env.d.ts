/// <reference types="vite/client" />

interface Window {
  __agentCore?: {
    audioFramesSent: () => number;
    disconnect: () => Promise<void>;
    reconnect?: () => Promise<void>;
    capturePrepared: () => boolean;
    workletLoaded: () => boolean;
    outputWorkletLoaded: () => boolean;
    playbackConsumed: () => number;
    playbackDiagnostics?: () => {
      consumed: number;
      queued: number;
      responseId: string | null;
      epoch: number;
      closed: boolean;
      rendered: Record<string, number>;
      completedResponses: string[];
      started: boolean;
      final: boolean;
    };
    audioOutputsReceived: () => number;
    captureStreaming: () => boolean;
    flushing?: () => boolean;
  };
}
