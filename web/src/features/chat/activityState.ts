export type StatusSource = {
  connection: string;
  pendingVoice: boolean;
  voiceLive: boolean;
  sessionStatus: string;
  inputState: string;
  outputState: string;
  liveResponseId: string | null;
  liveAssistantText?: string;
  liveAssistantHasContent?: boolean;
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
  if (source.sessionStatus === "ended") {
    return "Ended";
  }

  if (source.sessionStatus === "paused") {
    return "Paused";
  }

  if (source.connection === "idle") {
    return "Ready";
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

  if (source.sessionStatus === "ended") {
    return { kind: "idle" };
  }

  const live = source.liveResponseId != null;

  if (source.outputState === "interrupted" && live) {
    return { kind: "error", label: "Interrupted" };
  }

  if (source.inputState === "userSpeaking") {
    return { kind: "listening", label: "User speaking" };
  }

  if (source.outputState === "agentSpeaking" && live) {
    return { kind: "speaking", label: "Speaking…" };
  }

  if (source.outputState === "processingAttachments") {
    return { kind: "attachments", label: "Reading attachments…" };
  }

  if (source.outputState === "runningTools") {
    return { kind: "tools", label: "Running tools…" };
  }

  const hasLiveContent =
    Boolean(source.liveAssistantText?.trim()) || Boolean(source.liveAssistantHasContent);
  const generating = source.outputState === "agentGenerating" && live;
  if ((source.outputState === "waitingForAgent" && live) || generating || live) {
    if (hasLiveContent) {
      return { kind: "idle" };
    }

    return {
      kind: "thinking",
      label: generating ? "Generating response…" : "Thinking…"
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

export function pausedSessionMessage(reason: string | null | undefined): string {
  switch (reason) {
    case "inactivity":
      return "This conversation was paused after a period of inactivity. Resume to continue.";
    case "silentEvaluation":
      return "This conversation was paused after repeated quiet checks. Resume to continue.";
    case "initiative":
      return "This conversation was paused by the agent. Resume to continue.";
    case "manual":
      return "This conversation was paused. Resume to continue messaging.";
    default:
      return "This conversation is paused. Resume to continue messaging.";
  }
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
    text === "Paused" ||
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
