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
    emitClientSpeech?: (evidence: {
      kind: "started" | "partial" | "final" | "ended" | "failed";
      utteranceId: string;
      revision?: number;
      text?: string;
      confidence?: number;
      activityScore?: number;
      durationMs?: number;
    }) => Promise<void> | void;
    clientSpeechListening?: () => boolean;
    getUserMediaUsed?: () => boolean;
    speechDebug?: () => {
      sttTransport: string | null;
      ttsTransport: string | null;
      mode: string;
      attachmentId: string | null;
      error: string | null;
      evidenceAttempts?: number;
      userTexts?: string[];
    };
    holdFakeSpeechOutput?: () => void;
    releaseFakeSpeechOutput?: () => void;
    spokenClientSpeech?: () => string[];
    clientSpeechActive?: () => string | null;
    hubConnected?: () => boolean;
    sessionConnection?: () => string;
    captureLiveState?: () => boolean;
  };
  __agentCoreSpeechTest?: {
    fakeRecognizer?: boolean;
    fakeSynthesizer?: boolean;
  };
}
