export type StatusSource = {
  connection: string;
  pendingVoice: boolean;
  voiceLive: boolean;
  sessionStatus: string;
  inputState: string;
  outputState: string;
  liveResponseId: string | null;
  liveAssistantText?: string;
};

export type AgentActivityState =
  | { kind: "idle" }
  | { kind: "thinking"; label: string }
  | { kind: "attachments"; label: string }
  | { kind: "tools"; label: string }
  | { kind: "speaking"; label: string }
  | { kind: "listening"; label: string }
  | { kind: "reconnecting"; label: string }
  | { kind: "error"; label: string };

export function conversationStatus(source: StatusSource): string {
  return conversationStatusLabel(source);
}

function idleLabel(source: StatusSource): string {
  if (source.connection === "idle") {
    return "Ready";
  }

  if (source.sessionStatus === "ended" || source.sessionStatus === "ending") {
    return "Ended";
  }

  if (source.connection === "connecting") {
    return "Connecting";
  }

  return "Ready";
}

export function mapAgentActivity(source: StatusSource): AgentActivityState {
  if (source.connection === "failed") {
    return { kind: "error", label: "Connection failed. Check the network and try again." };
  }

  if (source.connection === "reconnecting") {
    return { kind: "reconnecting", label: "Reconnecting…" };
  }

  if (source.pendingVoice) {
    return { kind: "thinking", label: "Starting voice…" };
  }

  if (source.connection === "connecting") {
    return { kind: "thinking", label: "Connecting" };
  }

  if (source.connection === "idle") {
    return { kind: "idle" };
  }

  if (source.sessionStatus === "ended" || source.sessionStatus === "ending") {
    return { kind: "idle" };
  }

  if (source.outputState === "interrupted") {
    return { kind: "error", label: "Interrupted" };
  }

  if (source.inputState === "userSpeaking") {
    return { kind: "listening", label: "User speaking" };
  }

  if (source.outputState === "agentSpeaking") {
    return { kind: "speaking", label: "Speaking…" };
  }

  if (source.outputState === "processingAttachments") {
    return { kind: "attachments", label: "Reading attachments…" };
  }

  if (source.outputState === "runningTools") {
    return { kind: "tools", label: "Running tools…" };
  }

  const hasLiveText = Boolean(source.liveAssistantText?.trim());
  if (source.outputState === "agentGenerating" && hasLiveText) {
    return { kind: "idle" };
  }

  if (
    source.outputState === "waitingForAgent" ||
    source.outputState === "agentGenerating" ||
    source.liveResponseId != null
  ) {
    if (hasLiveText) {
      return { kind: "idle" };
    }

    return {
      kind: "thinking",
      label: source.outputState === "agentGenerating" ? "Generating response…" : "Thinking…"
    };
  }

  if (source.voiceLive) {
    return { kind: "listening", label: "Listening…" };
  }

  return { kind: "idle" };
}

export function conversationStatusLabel(source: StatusSource): string {
  const activity = mapAgentActivity(source);
  if (activity.kind === "idle") {
    return idleLabel(source);
  }

  return activity.label;
}

export function conversationStatusTone(text: string): "live" | "wait" | "alarm" {
  if (text.startsWith("Connection failed") || text === "Interrupted") {
    return "alarm";
  }

  if (
    text === "Connecting" ||
    text === "Reconnecting…" ||
    text === "Reconnecting" ||
    text === "Ended" ||
    text === "Starting voice…" ||
    text === "Thinking…" ||
    text === "Generating response…" ||
    text === "Reading attachments…" ||
    text === "Running tools…"
  ) {
    return "wait";
  }

  return "live";
}
