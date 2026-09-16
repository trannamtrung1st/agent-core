export type StatusSource = {
  connection: string;
  pendingVoice: boolean;
  voiceLive: boolean;
  sessionStatus: string;
  inputState: string;
  outputState: string;
  liveResponseId: string | null;
};

export function conversationStatus(source: StatusSource): string {
  if (source.connection === "failed") {
    return "Connection failed. Check the network and try again.";
  }

  if (source.connection === "reconnecting") {
    return "Reconnecting";
  }

  if (source.pendingVoice) {
    return "Starting voice…";
  }

  if (source.connection === "connecting") {
    return "Connecting";
  }

  if (source.connection === "idle") {
    return "Ready";
  }

  if (source.sessionStatus === "ended" || source.sessionStatus === "ending") {
    return "Ended";
  }

  if (source.outputState === "interrupted") {
    return "Interrupted";
  }

  if (source.inputState === "userSpeaking") {
    return "User speaking";
  }

  if (source.outputState === "agentSpeaking") {
    return "Agent speaking";
  }

  if (
    source.outputState === "waitingForAgent" ||
    source.outputState === "agentGenerating" ||
    source.liveResponseId != null
  ) {
    return "Thinking";
  }

  if (source.voiceLive) {
    return "Listening";
  }

  return "Ready";
}

export function conversationStatusTone(text: string): "live" | "wait" | "alarm" {
  if (text.startsWith("Connection failed") || text === "Interrupted") {
    return "alarm";
  }

  if (text === "Connecting" || text === "Reconnecting" || text === "Ended") {
    return "wait";
  }

  return "live";
}
