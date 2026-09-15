/// <reference types="vite/client" />

interface Window {
  __agentCore?: {
    audioFramesSent: () => number;
    disconnect: () => Promise<void>;
    capturePrepared: () => boolean;
    workletLoaded: () => boolean;
    outputWorkletLoaded: () => boolean;
    playbackConsumed: () => number;
    audioOutputsReceived: () => number;
    captureStreaming: () => boolean;
  };
}
