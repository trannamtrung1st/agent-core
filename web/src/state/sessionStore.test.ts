import { describe, expect, it } from "vitest";
import { applyServerEvent, emptySession, historyFromPayload, isReadonlySession, type ServerEvent } from "./sessionStore";

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
    state = applyServerEvent(
      state,
      event({
        type: "agent.block.upsert",
        sequence: 4,
        responseId: "r1",
        payload: { blockId: "b1", kind: "markdown", text: "**Hi**", fallbackText: "**Hi**" }
      })
    );
    expect(state.entries[0]?.blocks?.[0]?.kind).toBe("markdown");
  });

  it("replaces live blocks with session.ready history so reconnect hides unseen tails", () => {
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
      event({
        type: "agent.block.upsert",
        sequence: 2,
        responseId: "r1",
        payload: { blockId: "live", kind: "markdown", text: "**unseen**", fallbackText: "**unseen**" }
      })
    );
    expect(state.entries[0]?.blocks?.[0]?.blockId).toBe("live");
    state = applyServerEvent(
      state,
      event({
        type: "session.ready",
        sequence: 3,
        payload: {
          mode: "text",
          pendingMode: null,
          status: "attached",
          agent: { name: "Alex" },
          history: [
            {
              entryId: "e1",
              sequence: 1,
              role: "assistant",
              text: "Hello",
              responseId: "r1",
              status: "completed",
              deliveryMode: "text",
              blocks: [
                {
                  blockId: "b1",
                  kind: "markdown",
                  text: "**shown**",
                  fallbackText: "**shown**"
                }
              ]
            }
          ]
        }
      })
    );
    expect(state.entries[0]?.text).toBe("Hello");
    expect(state.entries[0]?.blocks).toEqual([
      {
        blockId: "b1",
        kind: "markdown",
        text: "**shown**",
        fallbackText: "**shown**",
        attachmentId: null,
        artifactId: null
      }
    ]);
    expect(state.entries[0]?.blocks?.some((block) => block.blockId === "live")).toBe(false);
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
    expect(state.preflightReady).toBe(true);
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

  it("clears leftover thinking output when the live response completes", () => {
    const thinking = {
      ...emptySession(),
      attachmentId: "a1",
      liveResponseId: "r1",
      outputState: "waitingForAgent",
      entries: [
        {
          entryId: "e1",
          sequence: 1,
          sourceEventId: null,
          role: "assistant" as const,
          text: "Hello",
          responseId: "r1",
          status: "streaming",
          deliveryMode: "text" as const,
          heardTextEndExclusive: 0,
          receivedTextEndExclusive: 5,
          createdAt: "2026-09-15T00:00:00.000Z"
        }
      ]
    };
    const completed = applyServerEvent(
      thinking,
      event({
        type: "agent.response.completed",
        sequence: 1,
        responseId: "r1",
        payload: { status: "completed" }
      })
    );
    expect(completed.liveResponseId).toBeNull();
    expect(completed.outputState).toBe("idle");
    expect(completed.entries[0]?.status).toBe("completed");
  });

  it("marks interrupted output when the live response is barged in", () => {
    const interrupted = applyServerEvent(
      {
        ...emptySession(),
        attachmentId: "a1",
        liveResponseId: "r1",
        outputState: "agentGenerating"
      },
      event({
        type: "agent.response.interrupted",
        sequence: 1,
        responseId: "r1",
        payload: { reason: "newText" }
      })
    );
    expect(interrupted.liveResponseId).toBeNull();
    expect(interrupted.outputState).toBe("interrupted");
  });

  it("keeps a newer live response in flight when an older one completes", () => {
    const state = applyServerEvent(
      {
        ...emptySession(),
        attachmentId: "a1",
        liveResponseId: "r2",
        outputState: "waitingForAgent"
      },
      event({
        type: "agent.response.completed",
        sequence: 1,
        responseId: "r1",
        payload: { status: "completed" }
      })
    );
    expect(state.liveResponseId).toBe("r2");
    expect(state.outputState).toBe("waitingForAgent");
  });

  it("lets a later state.changed replace inferred interrupted output", () => {
    const interrupted = applyServerEvent(
      {
        ...emptySession(),
        attachmentId: "a1",
        liveResponseId: "r1",
        outputState: "agentGenerating"
      },
      event({
        type: "agent.response.interrupted",
        sequence: 1,
        responseId: "r1",
        payload: { reason: "newText" }
      })
    );
    const waiting = applyServerEvent(
      interrupted,
      event({
        type: "session.state.changed",
        sequence: 2,
        payload: {
          status: "attached",
          mode: "text",
          pendingMode: null,
          muted: false,
          inputState: "idle",
          outputState: "waitingForAgent"
        }
      })
    );
    expect(waiting.liveResponseId).toBeNull();
    expect(waiting.outputState).toBe("waitingForAgent");
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

describe("historyFromPayload", () => {
  it("preserves finishReason on hydrated history rows", () => {
    const entries = historyFromPayload([
      {
        entryId: "e1",
        sequence: 1,
        sourceEventId: null,
        role: "assistant",
        text: "Truncated",
        responseId: "r1",
        status: "completed",
        deliveryMode: "text",
        heardTextEndExclusive: 9,
        receivedTextEndExclusive: 9,
        createdAt: "2026-09-18T00:00:00.000Z",
        finishReason: "lengthLimit"
      }
    ]);
    expect(entries[0]?.finishReason).toBe("lengthLimit");
  });
});

describe("isReadonlySession", () => {
  it("locks only terminal ended sessions", () => {
    expect(isReadonlySession({ sessionId: "s1", status: "ended" })).toBe(true);
    expect(isReadonlySession({ sessionId: "s1", status: "ending" })).toBe(false);
    expect(isReadonlySession({ sessionId: "s1", status: "attached" })).toBe(false);
    expect(isReadonlySession({ sessionId: null, status: "ended" })).toBe(false);
  });
});
