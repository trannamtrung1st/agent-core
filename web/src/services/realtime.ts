import { HttpTransportType, HubConnection, HubConnectionBuilder } from "@microsoft/signalr";
import { MessagePackHubProtocol } from "@microsoft/signalr-protocol-msgpack";
import { capture } from "../audio/capture";
import { useChatStore } from "../state/chatStore";
import { applyServerEvent, emptySession, type ServerEvent } from "../state/sessionStore";
import { createSession, endSession, listAgents } from "./api";

let connection: HubConnection | null = null;
let commandSequence = 0;
let audioFramesSent = 0;
let disposed = false;

function uuid(): string {
  return crypto.randomUUID();
}

export function audioFramesSentCount(): number {
  return audioFramesSent;
}

export function setDraft(draft: string): void {
  useChatStore.setState({ draft });
}

export function selectAgent(agentId: string): void {
  useChatStore.setState({ selectedAgentId: agentId });
}

function command(type: string, payload: Record<string, unknown>, sequence: number) {
  const snapshot = useChatStore.getState();
  return {
    protocolVersion: 1,
    sessionId: snapshot.sessionId,
    eventId: uuid(),
    sequence,
    timestamp: new Date().toISOString(),
    responseId: null,
    attachmentId: snapshot.attachmentId,
    type,
    payload
  };
}

async function invoke(method: string, type: string, payload: Record<string, unknown>, sequence: number) {
  if (!connection) {
    throw new Error("Not connected.");
  }

  return connection.invoke(method, command(type, payload, sequence)) as Promise<{
    accepted?: boolean;
    error?: { message?: string };
  }>;
}

function handleEvent(raw: ServerEvent): void {
  useChatStore.setState(applyServerEvent(useChatStore.getState(), raw));
  syncCapture();
}

function syncCapture(): void {
  const state = useChatStore.getState();
  if (state.connection !== "ready") {
    capture.release();
    return;
  }

  if (state.mode === "voice" && state.streamId && connection) {
    if (!capture.isStreaming()) {
      const hub = connection;
      const sessionId = state.sessionId;
      const attachmentId = state.attachmentId;
      const streamId = state.streamId;
      capture.start({
        sendAudio: async (frame) => {
          if (!hub || !sessionId || !attachmentId || !streamId) {
            return;
          }

          audioFramesSent += 1;
          await hub.send("SendAudio", {
            protocolVersion: 1,
            sessionId,
            attachmentId,
            streamId,
            frameSequence: frame.frameSequence,
            sampleOffset: frame.sampleOffset,
            data: frame.data
          });
        },
        speechStarted: async (utteranceId, sampleOffset, activityScore) => {
          commandSequence += 1;
          await invoke("SpeechStarted", "user.speech.started", {
            streamId,
            utteranceId,
            sampleOffset,
            activityScore
          }, commandSequence);
        },
        speechEnded: async (utteranceId, sampleOffset, activityScore) => {
          commandSequence += 1;
          await invoke("SpeechEnded", "user.speech.ended", {
            streamId,
            utteranceId,
            sampleOffset,
            durationMs: 0,
            activityScore
          }, commandSequence);
        }
      });
    }

    return;
  }

  if (state.pendingMode === "voice" || state.preflightReady) {
    return;
  }

  capture.release();
}

async function startConnection(sessionId: string): Promise<void> {
  await stopConnection();
  commandSequence = 0;
  audioFramesSent = 0;
  connection = new HubConnectionBuilder()
    .withUrl("/hubs/session", {
      skipNegotiation: true,
      transport: HttpTransportType.WebSockets
    })
    .withHubProtocol(new MessagePackHubProtocol())
    .withAutomaticReconnect([0, 2000, 5000, 10000])
    .build();
  connection.on("SessionEvent", handleEvent);
  connection.onreconnecting(() => {
    capture.release();
    useChatStore.setState({ connection: "reconnecting", pendingMode: null, preflightReady: false });
  });
  connection.onreconnected(async () => {
    commandSequence = 0;
    await invoke("Attach", "session.attach", { lastServerSequence: useChatStore.getState().lastServerSequence || null }, 0);
  });
  connection.onclose(() => {
    capture.release();
    if (!disposed) {
      useChatStore.setState({ connection: "reconnecting", pendingMode: null, preflightReady: false });
    }
  });
  useChatStore.setState({ connection: "connecting", sessionId, pendingMode: null, preflightReady: false });
  await connection.start();
  const ack = await invoke("Attach", "session.attach", { lastServerSequence: null }, 0);
  if (!ack?.accepted) {
    useChatStore.setState({ error: ack?.error?.message ?? "Attach failed.", connection: "failed" });
  }
}

async function stopConnection(): Promise<void> {
  capture.release();
  if (!connection) {
    return;
  }

  const current = connection;
  connection = null;
  current.off("SessionEvent");
  try {
    await current.stop();
  } catch {
    // ignored
  }
}

export async function bootstrap(): Promise<void> {
  const agents = await listAgents();
  useChatStore.setState({ agents, selectedAgentId: agents[0]?.id ?? "examiner" });
}

export async function startConversation(): Promise<void> {
  const created = await createSession(useChatStore.getState().selectedAgentId, "text");
  await startConnection(created.sessionId);
}

export async function sendDraft(): Promise<void> {
  const snapshot = useChatStore.getState();
  const text = snapshot.draft.trim();
  if (!text || snapshot.connection !== "ready") {
    return;
  }

  useChatStore.setState({
    draft: "",
    entries: [
      ...snapshot.entries,
      {
        entryId: uuid(),
        sequence: (snapshot.entries.at(-1)?.sequence ?? 0) + 0.5,
        sourceEventId: null,
        role: "user",
        text,
        responseId: null,
        status: "completed",
        deliveryMode: snapshot.mode,
        heardTextEndExclusive: text.length,
        receivedTextEndExclusive: text.length,
        createdAt: new Date().toISOString()
      }
    ]
  });
  commandSequence += 1;
  await invoke("SendText", "user.text", { text }, commandSequence);
}

export async function requestVoice(): Promise<void> {
  const snapshot = useChatStore.getState();
  if (!snapshot.voiceAvailable || snapshot.connection !== "ready") {
    return;
  }

  useChatStore.setState({ preflightReady: true, error: null });
  try {
    await capture.preflight();
  } catch (error) {
    capture.release();
    useChatStore.setState({
      preflightReady: false,
      error: error instanceof Error ? error.message : "Microphone preflight failed."
    });
    return;
  }
  commandSequence += 1;
  try {
    const ack = await invoke("SetMode", "session.mode.set", { mode: "voice" }, commandSequence);
    if (!ack?.accepted) {
      capture.release();
      useChatStore.setState({ preflightReady: false, error: ack?.error?.message ?? "Voice mode was rejected." });
    }
  } catch (error) {
    capture.release();
    useChatStore.setState({
      preflightReady: false,
      error: error instanceof Error ? error.message : "Voice mode failed."
    });
  }
}

export async function cancelVoice(): Promise<void> {
  capture.release();
  useChatStore.setState({ preflightReady: false });
  commandSequence += 1;
  await invoke("SetMode", "session.mode.set", { mode: "text" }, commandSequence);
}

export async function hangUp(): Promise<void> {
  disposed = true;
  const snapshot = useChatStore.getState();
  if (snapshot.sessionId) {
    commandSequence += 1;
    try {
      await invoke("EndSession", "session.end", { reason: "userEnded" }, commandSequence);
    } catch {
      await endSession(snapshot.sessionId);
    }
  }

  await stopConnection();
  capture.release();
  useChatStore.setState({
    ...emptySession(),
    agents: snapshot.agents,
    selectedAgentId: snapshot.selectedAgentId
  });
  disposed = false;
}

if (typeof window !== "undefined") {
  window.__agentCore = {
    audioFramesSent: audioFramesSentCount,
    disconnect: async () => {
      capture.release();
      await stopConnection();
      useChatStore.setState({ connection: "reconnecting", pendingMode: null, preflightReady: false });
    },
    capturePrepared: () => capture.isPrepared() || capture.isStreaming(),
    workletLoaded: () => capture.workletLoaded()
  };
}
