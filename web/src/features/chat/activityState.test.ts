import { conversationStatus, conversationStatusTone, mapAgentActivity, pausedSessionMessage } from "./activityState";

describe("agent activity mapping", () => {
  const ready = {
    connection: "ready",
    pendingVoice: false,
    voiceLive: false,
    sessionStatus: "attached",
    inputState: "idle",
    outputState: "idle",
    liveResponseId: null as string | null
  };

  it("keeps connection copy for reconnect, failure, pending voice and connecting", () => {
    expect(conversationStatus({ ...ready, connection: "reconnecting" })).toBe("Reconnecting…");
    expect(conversationStatus({ ...ready, connection: "failed" })).toContain("Connection failed");
    expect(conversationStatus({ ...ready, pendingVoice: true })).toBe("Starting voice…");
    expect(conversationStatus({ ...ready, connection: "connecting" })).toBe("Connecting");
    expect(conversationStatus({ ...ready, connection: "idle" })).toBe("Ready");
    expect(conversationStatus({ ...ready, connection: "idle", sessionStatus: "ended" })).toBe("Ended");
    expect(conversationStatus({ ...ready, sessionStatus: "ending" })).toBe("Ready");
  });

  it("maps runtime states onto in-flow activity labels", () => {
    expect(conversationStatus({ ...ready, outputState: "interrupted" })).toBe("Ready");
    expect(conversationStatus({ ...ready, outputState: "interrupted", liveResponseId: "r1" })).toBe("Interrupted");
    expect(conversationStatus({ ...ready, inputState: "userSpeaking" })).toBe("User speaking");
    expect(conversationStatus({ ...ready, outputState: "agentSpeaking" })).toBe("Ready");
    expect(conversationStatus({ ...ready, outputState: "agentSpeaking", liveResponseId: "r1" })).toBe("Speaking…");
    expect(conversationStatus({ ...ready, outputState: "waitingForAgent" })).toBe("Ready");
    expect(conversationStatus({ ...ready, outputState: "waitingForAgent", liveResponseId: "r1" })).toBe("Thinking…");
    expect(conversationStatus({ ...ready, outputState: "agentGenerating", liveResponseId: "r1" })).toBe(
      "Generating response…"
    );
    expect(conversationStatus({ ...ready, outputState: "agentGenerating" })).toBe("Ready");
    expect(conversationStatus({ ...ready, outputState: "processingAttachments" })).toBe("Reading attachments…");
    expect(conversationStatus({ ...ready, outputState: "runningTools" })).toBe("Running tools…");
    expect(conversationStatus({ ...ready, liveResponseId: "r1" })).toBe("Thinking…");
    expect(conversationStatus({ ...ready, voiceLive: true })).toBe("Listening…");
    expect(conversationStatus(ready)).toBe("Ready");
  });

  it("does not restore thinking from stale output after a terminal response", () => {
    expect(
      mapAgentActivity({
        ...ready,
        outputState: "waitingForAgent",
        liveResponseId: null,
        liveAssistantText: "Hello from synthetic."
      }).kind
    ).toBe("idle");
  });

  it("hides thinking once assistant text or blocks have started", () => {
    expect(
      mapAgentActivity({
        ...ready,
        outputState: "agentGenerating",
        liveResponseId: "r1",
        liveAssistantText: "Hello"
      }).kind
    ).toBe("idle");
    expect(
      mapAgentActivity({
        ...ready,
        outputState: "agentGenerating",
        liveResponseId: "r1",
        liveAssistantHasContent: true
      }).kind
    ).toBe("idle");
  });

  it("maps copy to live, wait, and alarm tones", () => {
    expect(conversationStatusTone("Ready")).toBe("live");
    expect(conversationStatusTone("Listening…")).toBe("live");
    expect(conversationStatusTone("Connecting")).toBe("wait");
    expect(conversationStatusTone("Generating response…")).toBe("wait");
    expect(conversationStatusTone("Reconnecting…")).toBe("wait");
    expect(conversationStatusTone("Interrupted")).toBe("alarm");
    expect(conversationStatusTone("Connection failed. Check the network and try again.")).toBe("alarm");
    expect(conversationStatus({ ...ready, sessionStatus: "paused" })).toBe("Paused");
    expect(conversationStatus({ ...ready, connection: "idle", sessionStatus: "ended" })).toBe("Ended");
  });

  it("uses reason-specific pause copy", () => {
    expect(pausedSessionMessage("inactivity")).toContain("inactivity");
    expect(pausedSessionMessage("silentEvaluation")).toContain("quiet checks");
    expect(pausedSessionMessage("initiative")).toContain("paused by the agent");
    expect(pausedSessionMessage("manual")).toBe("This conversation was paused. Resume to continue messaging.");
  });
});
