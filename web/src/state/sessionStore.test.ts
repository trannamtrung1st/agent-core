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

  it("reads session.ready speech transports", () => {
    const state = applyServerEvent(
      emptySession(),
      event({
        type: "session.ready",
        sequence: 1,
        payload: {
          mode: "text",
          pendingMode: null,
          status: "attached",
          agent: { name: "Alex", voiceAvailable: true },
          history: [],
          capabilities: {
            stt: { transport: "clientTranscript" },
            tts: { transport: "serverAudio" }
          }
        }
      })
    );
    expect(state.sttTransport).toBe("clientTranscript");
    expect(state.ttsTransport).toBe("serverAudio");
  });

  it("reads session.ready conversation language", () => {
    const state = applyServerEvent(
      emptySession(),
      event({
        type: "session.ready",
        sequence: 1,
        payload: {
          mode: "text",
          pendingMode: null,
          status: "attached",
          agent: { name: "Alex", voiceAvailable: true, language: "en" },
          history: [],
          capabilities: {
            stt: { transport: "clientTranscript" },
            tts: { transport: "clientSpeech" }
          }
        }
      })
    );
    expect(state.conversationLanguage).toBe("en");
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

  it("stores live user transcript on transcript.partial and clears on transcript.final", () => {
    const base = { ...emptySession(), attachmentId: "a1" };
    const partial = applyServerEvent(
      base,
      event({
        type: "transcript.partial",
        sequence: 1,
        payload: { utteranceId: "u1", revision: 1, text: "My favorite book is" }
      })
    );
    expect(partial.liveUserTranscript).toBe("My favorite book is");

    const finalState = applyServerEvent(
      partial,
      event({
        type: "transcript.final",
        sequence: 2,
        payload: {
          utteranceId: "u1",
          entryId: "e-user-1",
          entrySequence: 1,
          text: "My favorite book is Dune"
        }
      })
    );
    expect(finalState.liveUserTranscript).toBeNull();
    expect(finalState.entries.some((entry) => entry.text === "My favorite book is Dune")).toBe(true);
  });

  it("does not add durable history when transcript.final omits entryId", () => {
    const partial = applyServerEvent(
      { ...emptySession(), attachmentId: "a1" },
      event({
        type: "transcript.partial",
        sequence: 1,
        payload: { utteranceId: "u1", revision: 1, text: "hello" }
      })
    );
    const finalState = applyServerEvent(
      partial,
      event({
        type: "transcript.final",
        sequence: 2,
        payload: { utteranceId: "u1", text: "hello" }
      })
    );
    expect(finalState.liveUserTranscript).toBeNull();
    expect(finalState.entries).toHaveLength(0);
  });

  it("clears live user transcript on transcript.discarded", () => {
    const partial = applyServerEvent(
      { ...emptySession(), attachmentId: "a1" },
      event({
        type: "transcript.partial",
        sequence: 1,
        payload: { utteranceId: "u1", revision: 1, text: "mhm" }
      })
    );
    const discarded = applyServerEvent(
      partial,
      event({
        type: "transcript.discarded",
        sequence: 2,
        payload: { utteranceId: "u1" }
      })
    );
    expect(discarded.liveUserTranscript).toBeNull();
    expect(discarded.entries).toHaveLength(0);
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
        payload: { category: "Protocol", code: "ProtocolError", message: "Protocol error.", fatal: true }
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
    expect(next.sessionError?.fatal).toBe(true);
    expect(next.sessionError?.classId).toBe("validation/protocol");
  });

  it("keeps display receipts independent of heard offsets in live entries", () => {
    const state = applyServerEvent(
      { ...emptySession(), attachmentId: "a1", liveResponseId: "r1" },
      event({
        type: "agent.response.started",
        sequence: 1,
        responseId: "r1",
        payload: { entryId: "e1", entrySequence: 1, trigger: "userTurn" }
      })
    );
    expect(state.entries[0]?.receivedTextEndExclusive).toBe(0);
    expect(state.entries[0]?.heardTextEndExclusive).toBe(0);
  });

  it("maps error events to sanitized structured session errors", () => {
    const next = applyServerEvent(
      { ...emptySession(), attachmentId: "a1" },
      event({
        type: "error",
        sequence: 1,
        payload: {
          category: "Sandbox",
          code: "SandboxFailed",
          message: "Sandbox command failed.",
          fatal: false,
          retryAfterMs: 250,
          extensions: { apiKey: "sk-secret", retryable: true }
        }
      })
    );
    expect(next.sessionError?.classId).toBe("sandbox");
    expect(next.sessionError?.code).toBe("SandboxFailed");
    expect(next.sessionError?.retryAfterMs).toBe(250);
    expect(next.sessionError?.extensions).toEqual({ retryable: true });
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

  it("keeps display receipts independent of heard offsets and omits thinking rows", () => {
    const entries = historyFromPayload([
      {
        entryId: "e1",
        sequence: 1,
        role: "assistant",
        text: "Shown display.",
        responseId: "r1",
        status: "completed",
        deliveryMode: "voice",
        heardTextEndExclusive: 13,
        receivedTextEndExclusive: 14,
        createdAt: "2026-09-18T00:00:00.000Z"
      }
    ]);
    expect(entries[0]?.text).toBe("Shown display.");
    expect(entries[0]?.receivedTextEndExclusive).toBe(14);
    expect(entries[0]?.heardTextEndExclusive).toBe(13);
    expect(entries.some((entry) => entry.text.includes("Thinking"))).toBe(false);
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
