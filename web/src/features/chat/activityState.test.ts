import { conversationStatus, conversationStatusTone, mapAgentActivity, pausedSessionMessage } from "./activityState";

describe("agent activity mapping", () => {
  const ready = {
    connection: "ready",
    pendingVoice: false,
    voiceLive: false,
    clientTranscriptBlocked: false,
    sessionStatus: "attached",
    inputState: "idle",
    outputState: "idle",
    liveResponseId: null as string | null
  };

  it("keeps connection copy for reconnect, failure, pending voice and connecting", () => {
    expect(conversationStatus({ ...ready, connection: "reconnecting" })).toBe("Reconnecting to Agent Core…");
    expect(conversationStatus({ ...ready, connection: "failed" })).toContain("Connection lost");
    expect(conversationStatus({ ...ready, connection: "failed", connectionError: "This conversation is no longer available." }))
      .toBe("This conversation is no longer available.");
    expect(conversationStatus({ ...ready, pendingVoice: true })).toBe("Starting voice…");
    expect(conversationStatus({ ...ready, connection: "connecting" })).toBe("Connecting");
    expect(conversationStatus({ ...ready, connection: "idle" })).toBe("Ready");
    expect(conversationStatus({ ...ready, connection: "idle", sessionStatus: "ended" })).toBe("Ended");
    expect(conversationStatus({
      ...ready,
      connection: "idle",
      sessionStatus: "ended",
      lifecycleStatus: "completed"
    })).toBe("Completed");
    expect(conversationStatus({ ...ready, sessionStatus: "ending" })).toBe("Ready");
  });

  it("treats echo-only userSpeaking as listening when voice is live", () => {
    expect(
      conversationStatus({
        ...ready,
        voiceLive: true,
        inputState: "userSpeaking",
        outputState: "idle",
        liveResponseId: null
      })
    ).toBe("Listening…");
  });

  it("maps runtime states onto in-flow activity labels", () => {
    expect(conversationStatus({ ...ready, outputState: "interrupted" })).toBe("Ready");
    expect(conversationStatus({ ...ready, outputState: "interrupted", liveResponseId: "r1" })).toBe("Interrupted");
    expect(conversationStatus({ ...ready, inputState: "userSpeaking" })).toBe("Ready");
    expect(conversationStatus({ ...ready, inputState: "userSpeaking", liveUserTranscript: "Hello" })).toBe(
      "User speaking"
    );
    expect(
      conversationStatus({ ...ready, inputState: "userSpeaking", liveResponseId: "r1", outputState: "agentSpeaking" })
    ).toBe("User speaking");
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
    expect(
      conversationStatus({ ...ready, voiceLive: true, clientTranscriptBlocked: true })
    ).toBe("Voice input unavailable");
    expect(conversationStatus(ready)).toBe("Ready");
  });

  it("lets first-class progress outrank speaking and coarse output labels", () => {
    expect(
      conversationStatus({
        ...ready,
        liveResponseId: "r1",
        inputState: "userSpeaking",
        liveUserTranscript: "Hello",
        outputState: "agentSpeaking",
        activeProgress: { kind: "runningTool", message: "Running tools…" }
      })
    ).toBe("Running tools…");
    expect(
      mapAgentActivity({
        ...ready,
        liveResponseId: "r1",
        outputState: "waitingForAgent",
        activeProgress: { kind: "readingAttachments" }
      })
    ).toEqual({ kind: "attachments", label: "Reading attachments…" });
    expect(
      conversationStatus({
        ...ready,
        liveResponseId: "r1",
        outputState: "agentGenerating",
        activeProgress: { kind: "preparing" }
      })
    ).toBe("Preparing response…");
    expect(
      conversationStatus({
        ...ready,
        connection: "reconnecting",
        activeProgress: { kind: "runningTool", message: "Running tools…" }
      })
    ).toBe("Reconnecting to Agent Core…");
    expect(
      conversationStatus({
        ...ready,
        pendingVoice: true,
        activeProgress: { kind: "runningTool", message: "Running tools…" }
      })
    ).toBe("Starting voice…");
    expect(
      conversationStatus({
        ...ready,
        connection: "idle",
        activeProgress: { kind: "runningTool", message: "Running tools…" }
      })
    ).toBe("Ready");
    expect(
      conversationStatus({
        ...ready,
        sessionStatus: "ended",
        activeProgress: { kind: "runningTool", message: "Running tools…" }
      })
    ).toBe("Ended");
    expect(conversationStatusTone("Waiting…")).toBe("wait");
    expect(conversationStatusTone("Finalizing response…")).toBe("wait");
    expect(conversationStatusTone("Preparing response…")).toBe("wait");
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
    expect(conversationStatusTone("Voice input unavailable")).toBe("alarm");
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
