import { describe, expect, it } from "vitest";
import { applyServerEvent, emptySession, type ServerEvent } from "./sessionStore";

function event(partial: Partial<ServerEvent> & Pick<ServerEvent, "type" | "sequence">): ServerEvent {
  return {
    protocolVersion: 1,
    sessionId: "s1",
    attachmentId: "a1",
    eventId: `e${partial.sequence}`,
    timestamp: "2026-09-15T00:00:00.000Z",
    correlationId: "c1",
    causationId: null,
    responseId: null,
    payload: {},
    ...partial
  };
}

describe("applyServerEvent", () => {
  it("appends text by textStart and ignores duplicates", () => {
    let state = applyServerEvent(
      { ...emptySession(), attachmentId: "a1", liveResponseId: "r1" },
      event({
        type: "agent.response.started",
        sequence: 1,
        responseId: "r1",
        payload: { entryId: "e1", entrySequence: 1, trigger: "userTurn" }
      })
    );
    state = applyServerEvent(
      state,
      event({ type: "agent.text.delta", sequence: 2, responseId: "r1", payload: { text: "Hel", textStart: 0 } })
    );
    state = applyServerEvent(
      state,
      event({ type: "agent.text.delta", sequence: 2, responseId: "r1", payload: { text: "Hel", textStart: 0 } })
    );
    state = applyServerEvent(
      state,
      event({ type: "agent.text.delta", sequence: 3, responseId: "r1", payload: { text: "lo", textStart: 3 } })
    );
    expect(state.entries[0]?.text).toBe("Hello");
  });

  it("rejects late R1 deltas after R2 started", () => {
    let state = applyServerEvent(
      { ...emptySession(), attachmentId: "a1" },
      event({
        type: "agent.response.started",
        sequence: 1,
        responseId: "r1",
        payload: { entryId: "e1", entrySequence: 1, trigger: "userTurn" }
      })
    );
    state = applyServerEvent(
      state,
      event({
        type: "agent.response.interrupted",
        sequence: 2,
        responseId: "r1",
        payload: { reason: "newText", heardTextEndExclusive: 0 }
      })
    );
    state = applyServerEvent(
      state,
      event({
        type: "agent.response.started",
        sequence: 3,
        responseId: "r2",
        payload: { entryId: "e2", entrySequence: 2, trigger: "userTurn" }
      })
    );
    state = applyServerEvent(
      state,
      event({ type: "agent.text.delta", sequence: 4, responseId: "r1", payload: { text: "stale", textStart: 0 } })
    );
    expect(state.entries.find((entry) => entry.responseId === "r2")?.text).toBe("");
    expect(state.entries.find((entry) => entry.responseId === "r1")?.text).toBe("");
  });

  it("flags control sequence gaps", () => {
    const ready = applyServerEvent(
      emptySession(),
      event({
        type: "session.ready",
        sequence: 1,
        payload: { mode: "text", pendingMode: null, status: "attached", agent: { name: "Alex" }, history: [] }
      })
    );
    const gapped = applyServerEvent(
      { ...ready, error: "Protocol error.", errorFatal: true },
      event({ type: "agent.text.completed", sequence: 4, payload: { textLength: 1 } })
    );
    expect(gapped.connection).toBe("failed");
    expect(gapped.errorFatal).toBe(false);
    expect(gapped.error).toContain("Control sequence gap");
  });

  it("replaces history from ready and clears pendingMode", () => {
    const state = applyServerEvent(
      { ...emptySession(), pendingMode: "voice", preflightReady: true },
      event({
        type: "session.ready",
        sequence: 1,
        payload: {
          mode: "text",
          pendingMode: null,
          status: "attached",
          agent: { name: "Alex", role: "Examiner", voiceAvailable: true },
          history: [{ entryId: "e1", sequence: 1, role: "user", text: "Hi", deliveryMode: "text", status: "completed" }]
        }
      })
    );
    expect(state.pendingMode).toBeNull();
    expect(state.preflightReady).toBe(false);
    expect(state.entries).toHaveLength(1);
  });

  it("applies mute from session.state.changed without dropping voice mode", () => {
    const ready = applyServerEvent(
      emptySession(),
      event({
        type: "session.ready",
        sequence: 1,
        payload: { mode: "voice", pendingMode: null, status: "attached", agent: { name: "Alex" }, history: [], streamId: "s-1" }
      })
    );
    const muted = applyServerEvent(
      ready,
      event({
        type: "session.state.changed",
        sequence: 2,
        payload: { status: "attached", mode: "voice", pendingMode: null, muted: true, streamId: "s-1" }
      })
    );
    expect(muted.mode).toBe("voice");
    expect(muted.muted).toBe(true);
  });

  it("records input and output activity from session.state.changed", () => {
    const ready = applyServerEvent(
      emptySession(),
      event({
        type: "session.ready",
        sequence: 1,
        payload: { mode: "text", pendingMode: null, status: "attached", agent: { name: "Alex" }, history: [] }
      })
    );
    const thinking = applyServerEvent(
      ready,
      event({
        type: "session.state.changed",
        sequence: 2,
        payload: {
          status: "attached",
          mode: "text",
          pendingMode: null,
          muted: false,
          inputState: "idle",
          outputState: "agentGenerating"
        }
      })
    );
    expect(thinking.outputState).toBe("agentGenerating");
  });

  it("ready with durable voice does not mark capture live", () => {
    const state = applyServerEvent(
      { ...emptySession(), captureLive: false },
      event({
        type: "session.ready",
        sequence: 1,
        payload: { mode: "voice", pendingMode: null, status: "attached", agent: { name: "Alex" }, history: [], streamId: "s-1" }
      })
    );
    expect(state.mode).toBe("voice");
    expect(state.captureLive).toBe(false);
    expect(state.preflightReady).toBe(false);
  });

  it("clears a recoverable error on the next state change", () => {
    const base = { ...emptySession(), attachmentId: "a1" };
    const discontinuity = applyServerEvent(
      base,
      event({
        type: "error",
        sequence: 1,
        payload: { message: "Input audio sequence or sample offset gap.", code: "AudioDiscontinuity", fatal: false }
      })
    );
    expect(discontinuity.error).toContain("sample offset gap");
    const recovered = applyServerEvent(
      discontinuity,
      event({
        type: "session.state.changed",
        sequence: 2,
        payload: {
          status: "attached",
          mode: "voice",
          pendingMode: null,
          muted: false,
          inputState: "listening",
          outputState: "agentSpeaking",
          streamId: "s-2"
        }
      })
    );
    expect(recovered.error).toContain("sample offset gap");
    expect(recovered.inputState).toBe("listening");
    const later = applyServerEvent(
      recovered,
      event({
        type: "session.state.changed",
        sequence: 3,
        payload: {
          status: "attached",
          mode: "voice",
          pendingMode: null,
          muted: false,
          inputState: "userSpeaking",
          outputState: "idle"
        }
      })
    );
    expect(later.error).toBeNull();
  });

  it("keeps a fatal error through a recovered state change", () => {
    const base = { ...emptySession(), attachmentId: "a1" };
    const fatal = applyServerEvent(
      base,
      event({
        type: "error",
        sequence: 1,
        payload: { message: "Protocol error.", fatal: true }
      })
    );
    const next = applyServerEvent(
      fatal,
      event({
        type: "session.state.changed",
        sequence: 2,
        payload: { status: "attached", mode: "text", pendingMode: null, muted: false, inputState: "idle", outputState: "idle" }
      })
    );
    expect(next.error).toBe("Protocol error.");
  });
});
