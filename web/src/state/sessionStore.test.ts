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

  it("applies agent.speech.projection to the live assistant entry without completing it", () => {
    let state = applyServerEvent(
      { ...emptySession(), attachmentId: "a1", liveResponseId: "r1", mode: "voice" },
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
        type: "agent.speech.projection",
        sequence: 2,
        responseId: "r1",
        payload: { mode: "custom", text: "Spoken lead." }
      })
    );
    expect(state.entries[0]?.speechText).toBe("Spoken lead.");
    expect(state.entries[0]?.status).toBe("streaming");
    expect(state.liveResponseId).toBe("r1");
    state = applyServerEvent(
      state,
      event({
        type: "agent.speech.projection",
        sequence: 4,
        responseId: "r1",
        payload: { mode: "same", text: "Derived playback only." }
      })
    );
    expect(state.entries[0]?.speechText).toBe("Spoken lead.");
    state = applyServerEvent(
      state,
      event({ type: "agent.text.delta", sequence: 3, responseId: "r1", payload: { text: "# Title", textStart: 0 } })
    );
    expect(state.entries[0]?.text).toBe("# Title");
    expect(state.entries[0]?.speechText).toBe("Spoken lead.");
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
    expect(state.preflightReady).toBe(false);
    expect(state.mode).toBe("text");
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

  it("reads session.ready effective speech locale from capabilities, not only agent language", () => {
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
            speechLocale: { effective: "vi-VN", source: "sessionOverride", override: "vi-VN" },
            stt: { transport: "clientTranscript" },
            tts: { transport: "clientSpeech" }
          }
        }
      })
    );
    expect(state.conversationLanguage).toBe("en");
    expect(state.speechLocale).toBe("vi-VN");
    expect(state.speechLocaleSource).toBe("sessionOverride");
    expect(state.speechLocaleOverride).toBe("vi-VN");
  });

  it("reads session.ready model selection from capabilities", () => {
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
            model: {
              catalogKey: "scripted-beta",
              displayName: "Scripted Beta",
              selectionSource: "user",
              reasoningEffort: null,
              modelId: "scripted-beta"
            }
          }
        }
      })
    );
    expect(state.sessionModelKey).toBe("scripted-beta");
    expect(state.sessionModelDisplayName).toBe("Scripted Beta");
    expect(state.sessionModelSource).toBe("user");
    expect(state.sessionModelId).toBe("scripted-beta");
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

  it("records additive lifecycleStatus when the protocol status becomes ended", () => {
    const ready = applyServerEvent(
      emptySession(),
      event({
        type: "session.ready",
        sequence: 1,
        payload: { mode: "text", pendingMode: null, status: "attached", lifecycleStatus: "active", agent: { name: "Alex" }, history: [] }
      })
    );
    expect(ready.lifecycleStatus).toBe("active");
    const completed = applyServerEvent(
      ready,
      event({
        type: "session.state.changed",
        sequence: 2,
        payload: { status: "ended", lifecycleStatus: "completed", mode: "text", pendingMode: null, muted: false }
      })
    );
    expect(completed.status).toBe("ended");
    expect(completed.lifecycleStatus).toBe("completed");
    expect(completed.connection).toBe("idle");
    expect(completed.voiceAvailable).toBe(false);
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
        payload: { status: "completed", speechText: "Spoken hello" }
      })
    );
    expect(completed.liveResponseId).toBeNull();
    expect(completed.outputState).toBe("idle");
    expect(completed.entries[0]?.status).toBe("completed");
    expect(completed.entries[0]?.speechText).toBe("Spoken hello");
  });

  it("marks interrupted output when the live response is barged in", () => {
    const interrupted = applyServerEvent(
      {
        ...emptySession(),
        attachmentId: "a1",
        liveResponseId: "r1",
        outputState: "agentGenerating",
        entries: [
          {
            entryId: "a1",
            sequence: 1,
            sourceEventId: null,
            role: "assistant",
            text: "partial",
            responseId: "r1",
            status: "streaming",
            deliveryMode: "text",
            heardTextEndExclusive: 0,
            receivedTextEndExclusive: 3,
            createdAt: "2026-09-22T00:00:00.000Z"
          }
        ]
      },
      event({
        type: "agent.response.interrupted",
        sequence: 1,
        responseId: "r1",
        payload: { reason: "userBargeIn" }
      })
    );
    expect(interrupted.liveResponseId).toBeNull();
    expect(interrupted.outputState).toBe("interrupted");
    expect(interrupted.entries.find((entry) => entry.responseId === "r1")?.interruptReason).toBe("userBargeIn");
  });

  it("maps disconnected interruption reason on assistant entries", () => {
    const state = applyServerEvent(
      {
        ...emptySession(),
        attachmentId: "a1",
        liveResponseId: "r1",
        entries: [
          {
            entryId: "a1",
            sequence: 1,
            sourceEventId: null,
            role: "assistant",
            text: "partial",
            responseId: "r1",
            status: "streaming",
            deliveryMode: "text",
            heardTextEndExclusive: 0,
            receivedTextEndExclusive: 3,
            createdAt: "2026-09-22T00:00:00.000Z"
          }
        ]
      },
      event({
        type: "agent.response.interrupted",
        sequence: 2,
        responseId: "r1",
        payload: { reason: "disconnected" }
      })
    );
    expect(state.entries[0]?.status).toBe("interrupted");
    expect(state.entries[0]?.interruptReason).toBe("disconnected");
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

describe("agent.progress", () => {
  const progress = (sequence: number, extra: Partial<ServerEvent> & { payload: Record<string, unknown> }) =>
    event({
      type: "agent.progress",
      sequence,
      responseId: extra.responseId ?? "r1",
      ...extra
    });

  it("replaces a single activeProgress and does not append transcript entries", () => {
    let state = applyServerEvent(
      { ...emptySession(), attachmentId: "a1" },
      progress(1, {
        payload: { kind: "readingAttachments", state: "started", message: "Reading attachments…" }
      })
    );
    expect(state.activeProgress).toEqual({
      responseId: "r1",
      operationId: null,
      kind: "readingAttachments",
      state: "started",
      message: "Reading attachments…"
    });
    expect(state.entries).toEqual([]);
    state = applyServerEvent(
      state,
      progress(2, {
        payload: {
          kind: "runningTool",
          state: "started",
          operationId: "op-2",
          message: "Running tools…"
        }
      })
    );
    expect(state.activeProgress?.kind).toBe("runningTool");
    expect(state.activeProgress?.operationId).toBe("op-2");
    expect(state.entries).toEqual([]);
  });

  it("clears matching completed or failed progress", () => {
    let state = applyServerEvent(
      { ...emptySession(), attachmentId: "a1" },
      progress(1, { payload: { kind: "runningTool", state: "started", operationId: "op-1" } })
    );
    state = applyServerEvent(
      state,
      progress(2, { payload: { kind: "runningTool", state: "completed", operationId: "op-1" } })
    );
    expect(state.activeProgress).toBeNull();
    state = applyServerEvent(
      state,
      progress(3, { payload: { kind: "readingAttachments", state: "started" } })
    );
    state = applyServerEvent(
      state,
      progress(4, { payload: { kind: "readingAttachments", state: "failed" } })
    );
    expect(state.activeProgress).toBeNull();
  });

  it("clears progress on response completed and interrupted", () => {
    let state = applyServerEvent(
      { ...emptySession(), attachmentId: "a1", liveResponseId: "r1" },
      progress(1, { payload: { kind: "finalizing", state: "started" } })
    );
    state = applyServerEvent(
      state,
      event({ type: "agent.response.completed", sequence: 2, responseId: "r1", payload: { status: "completed" } })
    );
    expect(state.activeProgress).toBeNull();
    state = applyServerEvent(
      { ...emptySession(), attachmentId: "a1", liveResponseId: "r1" },
      progress(1, { payload: { kind: "runningTool", state: "started" } })
    );
    state = applyServerEvent(
      state,
      event({ type: "agent.response.interrupted", sequence: 2, responseId: "r1", payload: { reason: "userStop" } })
    );
    expect(state.activeProgress).toBeNull();
  });

  it("restores pending approval from session.ready during reattach", () => {
    const next = applyServerEvent(emptySession(), event({
      type: "session.ready",
      sequence: 1,
      payload: {
        mode: "text",
        status: "attached",
        history: [],
        activeResponseId: "resp-1",
        pendingApproval: {
          approvalId: "appr-1",
          responseId: "resp-1",
          operationId: "op-1",
          toolName: "email.send",
          effect: "sensitiveWrite",
          summary: "Send email",
          details: { to: "a@example.com" },
          expiresAt: "2026-09-22T12:00:00.000Z"
        }
      }
    }));
    expect(next.pendingApproval).toEqual({
      approvalId: "appr-1",
      responseId: "resp-1",
      operationId: "op-1",
      toolName: "email.send",
      effect: "sensitiveWrite",
      summary: "Send email",
      details: { to: "a@example.com" },
      expiresAt: "2026-09-22T12:00:00.000Z"
    });
  });

  it("clears progress on session.ready, paused state, and control-sequence reset", () => {
    const withProgress = {
      ...emptySession(),
      attachmentId: "a1",
      lastServerSequence: 1,
      activeProgress: {
        responseId: "r1",
        operationId: null,
        kind: "runningTool" as const,
        state: "started" as const,
        message: "Running tools…"
      }
    };
    const ready = applyServerEvent(
      withProgress,
      event({
        type: "session.ready",
        sequence: 9,
        payload: { mode: "text", status: "attached", history: [{ text: "Hello", role: "assistant" }] }
      })
    );
    expect(ready.activeProgress).toBeNull();
    expect(ready.entries.some((entry) => entry.text.includes("Running tools"))).toBe(false);
    const paused = applyServerEvent(
      { ...withProgress, lastServerSequence: 1 },
      event({
        type: "session.state.changed",
        sequence: 2,
        payload: { status: "paused", mode: "text", inputState: "idle", outputState: "idle" }
      })
    );
    expect(paused.activeProgress).toBeNull();
    const reset = applyServerEvent(
      { ...withProgress, lastServerSequence: 4 },
      event({ type: "agent.progress", sequence: 8, responseId: "r1", payload: { kind: "runningTool", state: "started" } })
    );
    expect(reset.connection).toBe("failed");
    expect(reset.activeProgress).toBeNull();
  });

  it("ignores stale response progress and does not restore it from history", () => {
    let state = applyServerEvent(
      {
        ...emptySession(),
        attachmentId: "a1",
        liveResponseId: "r2",
        tombstones: { r1: "interrupted" as const }
      },
      progress(1, { responseId: "r1", payload: { kind: "runningTool", state: "started" } })
    );
    expect(state.activeProgress).toBeNull();
    state = applyServerEvent(
      { ...emptySession(), attachmentId: "a1", liveResponseId: "r2" },
      progress(1, { responseId: "r1", payload: { kind: "runningTool", state: "started" } })
    );
    expect(state.activeProgress).toBeNull();
  });

  it("clears progress on first user-visible assistant text", () => {
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
      progress(2, { payload: { kind: "finalizing", state: "started", message: "Finalizing response…" } })
    );
    expect(state.activeProgress?.kind).toBe("finalizing");
    state = applyServerEvent(
      state,
      event({ type: "agent.text.delta", sequence: 3, responseId: "r1", payload: { text: "Hi", textStart: 0 } })
    );
    expect(state.activeProgress).toBeNull();
    expect(state.entries[0]?.text).toBe("Hi");
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

  it("maps public speechText from history payloads", () => {
    const entries = historyFromPayload([
      {
        entryId: "e1",
        sequence: 1,
        role: "assistant",
        text: "Shown display.",
        speechText: "Hidden speech",
        responseId: "r1",
        status: "completed",
        deliveryMode: "voice",
        heardTextEndExclusive: 13,
        receivedTextEndExclusive: 14,
        createdAt: "2026-09-18T00:00:00.000Z"
      }
    ]);
    expect(entries[0]?.speechText).toBe("Hidden speech");
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
