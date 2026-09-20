import { lifecycleOutcomeLabel } from "./sessionLifecycle";

export type StatusSource = {
  connection: string;
  pendingVoice: boolean;
  voiceLive: boolean;
  clientTranscriptBlocked: boolean;
  sessionStatus: string;
  lifecycleStatus?: string | null;
  inputState: string;
  outputState: string;
  liveResponseId: string | null;
  liveUserTranscript?: string | null;
  liveAssistantText?: string;
  liveAssistantHasContent?: boolean;
  connectionError?: string | null;
  activeProgress?: {
    kind: "preparing" | "readingAttachments" | "runningTool" | "waitingExternal" | "finalizing";
    message?: string | null;
  } | null;
};

function agentOutputLive(source: StatusSource): boolean {
  if (source.liveResponseId == null) {
    return false;
  }

  return (
    source.outputState === "agentSpeaking"
    || source.outputState === "agentGenerating"
    || source.outputState === "waitingForAgent"
    || source.outputState === "processingAttachments"
    || source.outputState === "runningTools"
    || source.outputState === "interrupted"
  );
}

/** Avoid echo-only VAD leaving "User speaking" after a typed turn or completed reply. */
function shouldShowUserSpeaking(source: StatusSource): boolean {
  if (source.inputState === "finalizing") {
    return true;
  }

  if (source.inputState !== "userSpeaking") {
    return false;
  }

  if (source.liveUserTranscript?.trim()) {
    return true;
  }

  return agentOutputLive(source);
}

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
    return lifecycleOutcomeLabel(source.lifecycleStatus);
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

export function progressActivityLabel(kind: NonNullable<StatusSource["activeProgress"]>["kind"], message?: string | null): string {
  const trusted = message?.trim();
  if (trusted) {
    return trusted;
  }

  switch (kind) {
    case "readingAttachments":
      return "Reading attachments…";
    case "runningTool":
      return "Running tools…";
    case "waitingExternal":
      return "Waiting…";
    case "finalizing":
      return "Finalizing response…";
    default:
      return "Preparing response…";
  }
}

function progressActivity(progress: NonNullable<StatusSource["activeProgress"]>): AgentActivityState {
  const label = progressActivityLabel(progress.kind, progress.message);
  if (progress.kind === "readingAttachments") {
    return { kind: "attachments", label };
  }

  if (progress.kind === "runningTool" || progress.kind === "waitingExternal") {
    return { kind: "tools", label };
  }

  return { kind: "thinking", label };
}

export function mapAgentActivity(source: StatusSource): AgentActivityState {
  if (source.connection === "failed") {
    const message = source.connectionError?.trim();
    return {
      kind: "error",
      label: message && message.length > 0 ? message : "Connection lost. Retry to continue."
    };
  }

  if (source.connection === "reconnecting") {
    return { kind: "reconnecting", label: "Reconnecting to Agent Core…" };
  }

  if (source.pendingVoice) {
    return { kind: "thinking", label: "Starting voice…" };
  }

  if (source.clientTranscriptBlocked && source.connection === "ready") {
    return { kind: "error", label: "Voice input unavailable" };
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

  if (source.activeProgress) {
    return progressActivity(source.activeProgress);
  }

  const live = source.liveResponseId != null;

  if (source.outputState === "interrupted" && live) {
    return { kind: "error", label: "Interrupted" };
  }

  if (shouldShowUserSpeaking(source)) {
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
  if (text.startsWith("Connection failed") || text === "Interrupted" || text === "Voice input unavailable") {
    return "alarm";
  }

  if (
    text === "Connecting" ||
    text === "Reconnecting…" ||
    text === "Reconnecting" ||
    text === "Ended" ||
    text === "Completed" ||
    text === "Expired" ||
    text === "Cancelled" ||
    text === "Paused" ||
    text === "Starting voice…" ||
    text === "Thinking…" ||
    text === "Generating response…" ||
    text === "Reading attachments…" ||
    text === "Running tools…" ||
    text === "Waiting…" ||
    text === "Finalizing response…" ||
    text === "Preparing response…"
  ) {
    return "wait";
  }

  return "live";
}
