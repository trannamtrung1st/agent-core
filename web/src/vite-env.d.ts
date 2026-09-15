/// <reference types="vite/client" />

interface Window {
  __agentCore?: {
    audioFramesSent: () => number;
    disconnect: () => Promise<void>;
  };
}
