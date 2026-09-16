import { conversationStatus, conversationStatusTone } from "./statusLabel";

describe("conversationStatus", () => {
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
    expect(conversationStatus({ ...ready, connection: "reconnecting" })).toBe("Reconnecting");
    expect(conversationStatus({ ...ready, connection: "failed" })).toContain("Connection failed");
    expect(conversationStatus({ ...ready, pendingVoice: true })).toBe("Starting voice…");
    expect(conversationStatus({ ...ready, connection: "connecting" })).toBe("Connecting");
    expect(conversationStatus({ ...ready, connection: "idle" })).toBe("Ready");
  });

  it("derives controller labels from input and output evidence", () => {
    expect(conversationStatus({ ...ready, outputState: "interrupted" })).toBe("Interrupted");
    expect(conversationStatus({ ...ready, inputState: "userSpeaking" })).toBe("User speaking");
    expect(conversationStatus({ ...ready, outputState: "agentSpeaking" })).toBe("Agent speaking");
    expect(conversationStatus({ ...ready, outputState: "waitingForAgent" })).toBe("Thinking");
    expect(conversationStatus({ ...ready, outputState: "runningTools" })).toBe("Thinking");
    expect(conversationStatus({ ...ready, liveResponseId: "r1" })).toBe("Thinking");
    expect(conversationStatus({ ...ready, voiceLive: true })).toBe("Listening");
    expect(conversationStatus(ready)).toBe("Ready");
  });

  it("maps copy to live, wait, and alarm tones", () => {
    expect(conversationStatusTone("Ready")).toBe("live");
    expect(conversationStatusTone("Listening")).toBe("live");
    expect(conversationStatusTone("Connecting")).toBe("wait");
    expect(conversationStatusTone("Reconnecting")).toBe("wait");
    expect(conversationStatusTone("Interrupted")).toBe("alarm");
    expect(conversationStatusTone("Connection failed. Check the network and try again.")).toBe("alarm");
  });
});
