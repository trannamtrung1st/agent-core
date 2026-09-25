import { afterEach, describe, expect, it, vi } from "vitest";

vi.mock("./api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./api")>();
  return {
    ...actual,
    listSessionMessages: vi.fn()
  };
});
import { capture } from "../audio/capture";
import { encodePcm16Le } from "../audio/pcm";
import { emptySession, useSessionStore, type ServerEvent } from "../state/sessionStore";
import { listSessionMessages } from "./api";
import { realtimeTestHooks, composerSendEnabled, composerStopEnabled, reportCommittedEntries, requestVoice, sendDraft, cancelRenderedResponse, setMuted, hangUp, cancelVoice, startConversation } from "./realtime";

const hooks = realtimeTestHooks!;

function pcmFrame(samples = 480): Uint8Array {
  return encodePcm16Le(new Float32Array(samples).fill(0.1));
}

describe("realtime race handling", () => {
  afterEach(() => {
    capture.release();
    hooks.resetOutput();
    hooks.setConnection(null);
    window.localStorage.clear();
    useSessionStore.setState({
      ...emptySession(),
      agents: [],
      selectedAgentId: "examiner"
    });
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    delete window.__agentCoreSpeechTest;
    vi.useRealTimers();
  });

  it("sequence gap aborts playback and reattaches before applying started side effects", async () => {
    const flush = vi.spyOn(capture, "flushPlayback").mockResolvedValue(0);
    const stop = vi.fn().mockResolvedValue(undefined);
    const start = vi.fn().mockResolvedValue(undefined);
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn(), stop, start } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      lastServerSequence: 2,
      agents: [],
      selectedAgentId: "examiner"
    });

    const gapEvent: ServerEvent = {
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "gap",
      sequence: 4,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: "r-gap",
      type: "agent.response.started",
      payload: { entryId: "e1", entrySequence: 1 }
    };

    hooks.handleEvent(gapEvent);

    await vi.waitFor(() => {
      expect(flush).toHaveBeenCalled();
      expect(stop).toHaveBeenCalled();
      expect(start).toHaveBeenCalled();
      expect(invoke).toHaveBeenCalledWith("Attach", expect.objectContaining({ type: "session.attach" }));
    });
    expect(useSessionStore.getState().liveResponseId).toBeNull();
    expect(useSessionStore.getState().connection).toBe("reconnecting");
    expect(useSessionStore.getState().error).toBeNull();
  });

  it("clears errorFatal when reconnect attach is rejected", async () => {
    const stop = vi.fn().mockResolvedValue(undefined);
    const start = vi.fn().mockResolvedValue(undefined);
    const invoke = vi.fn().mockResolvedValue({ accepted: false, error: { message: "Reconnect failed." } });
    hooks.setConnection({ invoke, send: vi.fn(), stop, start } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      lastServerSequence: 2,
      error: "Protocol error.",
      errorFatal: true,
      agents: [],
      selectedAgentId: "examiner"
    });

    const gapEvent: ServerEvent = {
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "gap",
      sequence: 4,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: "r-gap",
      type: "agent.response.started",
      payload: { entryId: "e1", entrySequence: 1 }
    };

    hooks.handleEvent(gapEvent);
    await vi.waitFor(() => {
      expect(invoke).toHaveBeenCalledWith("Attach", expect.objectContaining({ type: "session.attach" }));
    });
    expect(useSessionStore.getState().error).toBe("Reconnect failed.");
    expect(useSessionStore.getState().errorFatal).toBe(false);
    expect(useSessionStore.getState().connection).toBe("failed");
  });

  it("playback.completed waits for worklet complete before sending", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    vi.spyOn(capture, "enqueuePlayback").mockReturnValue(true);
    vi.spyOn(capture, "playbackQueued").mockReturnValue(12);
    vi.spyOn(capture, "playbackConsumed").mockReturnValue(480);
    const completeHolder: { fn: ((responseId: string, consumed: number) => void) | null } = { fn: null };
    vi.spyOn(capture, "setPlaybackCompleteListener").mockImplementation((listener) => {
      completeHolder.fn = listener as ((responseId: string, consumed: number) => void) | null;
    });

    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      mode: "voice",
      agents: [],
      selectedAgentId: "examiner"
    });

    hooks.markOutputStarted("r1");
    hooks.handleAudioOutput({
      sessionId: "s1",
      attachmentId: "a1",
      responseId: "r1",
      frameSequence: 1,
      sampleOffset: 0,
      data: pcmFrame(),
      isFinal: true
    });

    expect(invoke.mock.calls.some(([method]) => method === "PlaybackCompleted")).toBe(false);

    completeHolder.fn?.("r1", 480);
    await vi.waitFor(() => {
      expect(invoke.mock.calls.some(([method]) => method === "PlaybackCompleted")).toBe(true);
    });
  });

  it("sends display receipts in voice without copying them onto playback", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      lastServerSequence: 1,
      mode: "voice",
      agents: [],
      selectedAgentId: "examiner"
    });

    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "started",
      sequence: 2,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: "r-voice",
      type: "agent.response.started",
      payload: { entryId: "e1", entrySequence: 1 }
    });

    reportCommittedEntries([
      {
        role: "assistant",
        responseId: "r-voice",
        text: "Hello from synthetic.",
        status: "streaming"
      }
    ]);

    await vi.waitFor(() => {
      expect(invoke).toHaveBeenCalledWith(
        "ResponseReceived",
        expect.objectContaining({
          type: "response.received",
          responseId: "r-voice",
          payload: expect.objectContaining({
            textEndExclusive: "Hello from synthetic.".length
          })
        })
      );
    });
    expect(invoke.mock.calls.some(([method]) => method === "PlaybackProgress" || method === "PlaybackCompleted")).toBe(false);
  });

  it("sends a display receipt when committed blocks change without more text", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      lastServerSequence: 1,
      liveResponseId: "r-blocks",
      agents: [],
      selectedAgentId: "examiner"
    });

    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "started",
      sequence: 2,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: "r-blocks",
      type: "agent.response.started",
      payload: { entryId: "e1", entrySequence: 1 }
    });

    reportCommittedEntries([
      {
        role: "assistant",
        responseId: "r-blocks",
        text: "Hello from synthetic.",
        status: "streaming"
      }
    ]);

    await vi.waitFor(() => {
      expect(invoke).toHaveBeenCalledWith(
        "ResponseReceived",
        expect.objectContaining({
          type: "response.received",
          responseId: "r-blocks",
          payload: expect.objectContaining({
            textEndExclusive: "Hello from synthetic.".length,
            blockIds: []
          })
        })
      );
    });

    invoke.mockClear();
    reportCommittedEntries([
      {
        role: "assistant",
        responseId: "r-blocks",
        text: "Hello from synthetic.",
        status: "streaming",
        blocks: [{ blockId: "md-1" }]
      }
    ]);

    await vi.waitFor(() => {
      expect(invoke).toHaveBeenCalledWith(
        "ResponseReceived",
        expect.objectContaining({
          type: "response.received",
          responseId: "r-blocks",
          payload: expect.objectContaining({
            textEndExclusive: "Hello from synthetic.".length,
            blockIds: ["md-1"]
          })
        })
      );
    });

    invoke.mockClear();
    reportCommittedEntries([
      {
        role: "assistant",
        responseId: "r-blocks",
        text: "Hello from synthetic.",
        status: "streaming",
        blocks: [{ blockId: "md-1" }]
      }
    ]);
    await Promise.resolve();
    expect(invoke).not.toHaveBeenCalled();
  });

  it("duplicate-sequence playback.stop does not start a second flush", async () => {
    const flush = vi.spyOn(capture, "flushPlayback").mockResolvedValue(0);
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      lastServerSequence: 3,
      liveResponseId: "r1",
      agents: [],
      selectedAgentId: "examiner"
    });

    const stopEvent: ServerEvent = {
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "stop-1",
      sequence: 4,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: "r1",
      type: "playback.stop",
      payload: { reason: "interrupted" }
    };

    hooks.handleEvent(stopEvent);
    hooks.handleEvent({ ...stopEvent, eventId: "stop-dup" });
    await vi.waitFor(() => expect(flush).toHaveBeenCalledTimes(1));
  });

  it("expired early audio rejects a later offset-0 frame", async () => {
    vi.useFakeTimers();
    const enqueue = vi.spyOn(capture, "enqueuePlayback").mockReturnValue(true);
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      agents: [],
      selectedAgentId: "examiner"
    });

    hooks.handleAudioOutput({
      sessionId: "s1",
      attachmentId: "a1",
      responseId: "r-early",
      frameSequence: 1,
      sampleOffset: 0,
      data: pcmFrame(),
      isFinal: false
    });
    expect(enqueue).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(60);
    hooks.handleAudioOutput({
      sessionId: "s1",
      attachmentId: "a1",
      responseId: "r-early",
      frameSequence: 1,
      sampleOffset: 0,
      data: pcmFrame(),
      isFinal: false
    });
    expect(enqueue).not.toHaveBeenCalled();
    vi.useRealTimers();
  });

  it("keeps the draft when SendText is rejected", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: false, error: { message: "Mailbox saturated." } });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      draft: "Hello",
      agents: [],
      selectedAgentId: "examiner"
    });
    await sendDraft();
    expect(useSessionStore.getState().draft).toBe("Hello");
    expect(useSessionStore.getState().entries).toHaveLength(0);
    expect(useSessionStore.getState().error).toContain("Mailbox saturated");
  });

  it("shows the user entry before SendText ack and keeps user before assistant when response starts first", async () => {
    let resolveAck: (value: { accepted: boolean }) => void = () => undefined;
    const invoke = vi.fn().mockImplementation(
      () => new Promise<{ accepted: boolean }>((resolve) => {
        resolveAck = resolve;
      })
    );
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      draft: "Hello",
      agents: [],
      selectedAgentId: "examiner"
    });
    const sending = sendDraft();
    await Promise.resolve();
    const optimistic = useSessionStore.getState().entries;
    expect(optimistic).toHaveLength(1);
    expect(optimistic[0]?.role).toBe("user");
    expect(optimistic[0]?.text).toBe("Hello");
    expect(optimistic[0]?.status).toBe("sending");
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "assistant-started",
      sequence: 2,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: "r1",
      type: "agent.response.started",
      payload: { entryId: "a-entry", entrySequence: 2 }
    });
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "assistant-delta",
      sequence: 3,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: "r1",
      type: "agent.text.delta",
      payload: { text: "Hi there", textStart: 0 }
    });
    const raced = useSessionStore.getState().entries;
    expect(raced.map((entry) => entry.role)).toEqual(["user", "assistant"]);
    resolveAck({ accepted: true });
    await sending;
    const finalEntries = useSessionStore.getState().entries;
    expect(finalEntries.map((entry) => entry.role)).toEqual(["user", "assistant"]);
    expect(finalEntries[0]?.status).toBe("completed");
    expect(finalEntries[1]?.text).toBe("Hi there");
  });

  it("removes the optimistic user entry when SendText is rejected after optimistic insert", async () => {
    let rejectAck: (value: { accepted: boolean; error: { message: string } }) => void = () => undefined;
    const invoke = vi.fn().mockImplementation(
      () => new Promise((resolve) => {
        rejectAck = resolve;
      })
    );
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      draft: "Hello",
      agents: [],
      selectedAgentId: "examiner"
    });
    const sending = sendDraft();
    await Promise.resolve();
    expect(useSessionStore.getState().entries).toHaveLength(1);
    rejectAck({ accepted: false, error: { message: "Mailbox saturated." } });
    await sending;
    expect(useSessionStore.getState().entries).toHaveLength(0);
    expect(useSessionStore.getState().draft).toBe("Hello");
  });

  it("does not restore a rejected draft over text typed while sending", async () => {
    let rejectAck: (value: { accepted: boolean; error: { message: string } }) => void = () => undefined;
    const invoke = vi.fn().mockImplementation(
      () => new Promise((resolve) => {
        rejectAck = resolve;
      })
    );
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      draft: "Hello",
      agents: [],
      selectedAgentId: "examiner"
    });
    const sending = sendDraft();
    await Promise.resolve();
    useSessionStore.setState({ draft: "typed later" });
    rejectAck({ accepted: false, error: { message: "Mailbox saturated." } });
    await sending;
    expect(useSessionStore.getState().draft).toBe("typed later");
  });

  it("does not send a second draft while SendText is in flight", async () => {
    let resolveAck: (value: { accepted: boolean }) => void = () => undefined;
    const invoke = vi.fn().mockImplementation(
      () => new Promise<{ accepted: boolean }>((resolve) => {
        resolveAck = resolve;
      })
    );
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      draft: "Hello",
      agents: [],
      selectedAgentId: "examiner"
    });
    const first = sendDraft();
    const second = sendDraft();
    await Promise.resolve();
    expect(invoke).toHaveBeenCalledTimes(1);
    expect(useSessionStore.getState().draft).toBe("");
    expect(useSessionStore.getState().entries).toHaveLength(1);
    resolveAck({ accepted: true });
    await Promise.all([first, second]);
    expect(invoke).toHaveBeenCalledTimes(1);
    expect(useSessionStore.getState().entries).toHaveLength(1);
  });

  it("sends first-party text immediately when no response is live", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      draft: "Hello",
      agents: [],
      selectedAgentId: "examiner"
    });
    await sendDraft();
    expect(invoke).toHaveBeenCalledWith(
      "SendText",
      expect.objectContaining({
        type: "user.text",
        payload: expect.objectContaining({ text: "Hello" })
      })
    );
    expect(invoke.mock.calls[0][1].payload).toEqual(
      expect.objectContaining({ text: "Hello", behavior: "queue" })
    );
  });

  it("queues locally after SendText ack before agent.response.started", async () => {
    let resolveAck: (value: { accepted: boolean }) => void = () => undefined;
    const invoke = vi.fn().mockImplementation(
      () => new Promise((resolve) => {
        resolveAck = resolve;
      })
    );
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      draft: "first",
      outputState: "idle",
      liveResponseId: null,
      agents: [],
      selectedAgentId: "examiner"
    });
    const first = sendDraft();
    await Promise.resolve();
    expect(invoke).toHaveBeenCalledTimes(1);
    useSessionStore.setState({ draft: "second" });
    const second = sendDraft();
    await Promise.resolve();
    expect(invoke).toHaveBeenCalledTimes(1);
    expect(useSessionStore.getState().pendingSendQueue).toHaveLength(1);
    expect(useSessionStore.getState().pendingSendQueue[0]?.text).toBe("second");
    resolveAck({ accepted: true });
    await Promise.all([first, second]);
    expect(useSessionStore.getState().liveResponseId).toBeNull();
  });

  it("clears awaiting when response starts before SendText ack completes", async () => {
    let resolveAck: (value: { accepted: boolean }) => void = () => undefined;
    const invoke = vi.fn().mockImplementation(
      () => new Promise((resolve) => {
        resolveAck = resolve;
      })
    );
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      draft: "Hello",
      agents: [],
      selectedAgentId: "examiner"
    });
    const sending = sendDraft();
    await Promise.resolve();
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "e-started",
      sequence: 1,
      timestamp: "2026-09-22T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: "r1",
      type: "agent.response.started",
      payload: { entryId: "a1", entrySequence: 1 }
    });
    resolveAck({ accepted: true });
    await sending;
    useSessionStore.setState({ draft: "follow-up" });
    await sendDraft();
    expect(invoke).toHaveBeenCalledTimes(1);
    expect(useSessionStore.getState().pendingSendQueue).toHaveLength(1);
    expect(useSessionStore.getState().pendingSendQueue[0]?.text).toBe("follow-up");
  });

  it("treats in-flight output states as composer busy", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      draft: "Hello",
      outputState: "waitingForAgent",
      liveResponseId: null,
      agents: [],
      selectedAgentId: "examiner"
    });
    await sendDraft();
    expect(invoke).not.toHaveBeenCalled();
    expect(useSessionStore.getState().pendingSendQueue).toHaveLength(1);
  });

  it("enables Stop while voice playback continues after the live response completes", () => {
    vi.spyOn(capture, "playbackResponseId").mockReturnValue("r1");
    vi.spyOn(capture, "playbackClosed").mockReturnValue(false);
    vi.spyOn(capture, "playbackQueued").mockReturnValue(0);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      mode: "voice",
      liveResponseId: null,
      voicePlaybackResponseId: "r1"
    });
    expect(composerStopEnabled()).toBe(true);
  });

  it("cancels the live response after flushing a different voice playback target", async () => {
    vi.spyOn(capture, "playbackResponseId").mockReturnValue("r1");
    vi.spyOn(capture, "playbackClosed").mockReturnValue(false);
    vi.spyOn(capture, "playbackQueued").mockReturnValue(1);
    vi.spyOn(capture, "playbackEpoch").mockReturnValue(1);
    vi.spyOn(capture, "flushPlayback").mockResolvedValue(100);
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      mode: "voice",
      liveResponseId: "r2",
      voicePlaybackResponseId: "r1",
      agents: [],
      selectedAgentId: "examiner"
    });
    await cancelRenderedResponse();
    expect(capture.flushPlayback).toHaveBeenCalledWith("r1");
    expect(invoke).toHaveBeenCalledWith(
      "CancelResponse",
      expect.objectContaining({
        type: "agent.response.cancel",
        responseId: "r2"
      })
    );
  });

  it("does not dequeue or SendText when Stop only flushes voice playback", async () => {
    vi.spyOn(capture, "playbackResponseId").mockReturnValue("r1");
    vi.spyOn(capture, "playbackClosed").mockReturnValue(false);
    vi.spyOn(capture, "playbackQueued").mockReturnValue(1);
    vi.spyOn(capture, "playbackEpoch").mockReturnValue(1);
    vi.spyOn(capture, "flushPlayback").mockResolvedValue(100);
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    const queued = {
      localId: "q1",
      eventId: "e-wait",
      text: "Wait",
      attachmentIds: [] as string[],
      attachments: []
    };
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      mode: "voice",
      liveResponseId: null,
      voicePlaybackResponseId: "r1",
      pendingSendQueue: [queued],
      agents: [],
      selectedAgentId: "examiner"
    });
    await cancelRenderedResponse();
    expect(capture.flushPlayback).toHaveBeenCalledWith("r1");
    expect(invoke).not.toHaveBeenCalled();
    expect(useSessionStore.getState().pendingSendQueue).toEqual([queued]);
  });

  it("queues locally instead of SendText while a response is live", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      draft: "Hello",
      liveResponseId: "r1",
      agents: [],
      selectedAgentId: "examiner"
    });
    expect(composerStopEnabled()).toBe(true);
    await sendDraft();
    expect(invoke).not.toHaveBeenCalled();
    expect(useSessionStore.getState().pendingSendQueue).toHaveLength(1);
  });

  it("cancels the rendered responseId and ignores stale completion after a newer live response", async () => {
    let resolveAck: (value: { accepted: boolean; error: { code: string; message: string } }) => void = () => undefined;
    const invoke = vi.fn().mockImplementation(
      () => new Promise((resolve) => {
        resolveAck = resolve;
      })
    );
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      liveResponseId: "r1",
      draft: "queued later",
      agents: [],
      selectedAgentId: "examiner"
    });
    const stopping = cancelRenderedResponse();
    await Promise.resolve();
    expect(invoke).toHaveBeenCalledWith(
      "CancelResponse",
      expect.objectContaining({
        type: "agent.response.cancel",
        responseId: "r1",
        payload: {}
      })
    );
    useSessionStore.setState({
      liveResponseId: "r2",
      entries: [
        {
          entryId: "a2",
          sequence: 2,
          sourceEventId: null,
          role: "assistant",
          text: "newer",
          responseId: "r2",
          status: "streaming",
          deliveryMode: "text",
          heardTextEndExclusive: 0,
          receivedTextEndExclusive: 5,
          createdAt: "2026-09-18T00:00:00.000Z"
        }
      ]
    });
    resolveAck({ accepted: false, error: { code: "StaleCommand", message: "A newer response is active." } });
    await stopping;
    expect(useSessionStore.getState().liveResponseId).toBe("r2");
    expect(useSessionStore.getState().entries[0]?.text).toBe("newer");
    expect(useSessionStore.getState().draft).toBe("queued later");
    expect(useSessionStore.getState().error).toBeNull();
  });

  it("clears errorFatal when mute is rejected", async () => {
    vi.spyOn(capture, "muteInput").mockResolvedValue(undefined);
    vi.spyOn(capture, "isPrepared").mockReturnValue(true);
    vi.spyOn(capture, "isStreaming").mockReturnValue(false);
    const start = vi.spyOn(capture, "start").mockResolvedValue(undefined);
    const invoke = vi.fn().mockResolvedValue({ accepted: false, error: { message: "Mute failed." } });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      mode: "voice",
      streamId: "stream-1",
      error: "Previous fatal error.",
      errorFatal: true,
      agents: [],
      selectedAgentId: "examiner"
    });
    await setMuted(true);
    expect(useSessionStore.getState().error).toBe("Mute failed.");
    expect(useSessionStore.getState().errorFatal).toBe(false);
    expect(start).toHaveBeenCalled();
  });

  it("does not preflight when voice is already prepared", async () => {
    const preflight = vi.spyOn(capture, "preflight");
    vi.spyOn(capture, "isPrepared").mockReturnValue(true);
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      mode: "voice",
      voiceAvailable: true,
      agents: [],
      selectedAgentId: "examiner"
    });
    await requestVoice();
    expect(preflight).not.toHaveBeenCalled();
    expect(invoke).not.toHaveBeenCalled();
  });

  it("falls back to HTTP end when EndSession is rejected", async () => {
    window.localStorage.setItem("agent-core.owner-capability", "tok");
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 204 });
    vi.stubGlobal("fetch", fetchMock);
    const invoke = vi.fn().mockResolvedValue({ accepted: false, error: { message: "Persistent save failed." } });
    const stop = vi.fn().mockResolvedValue(undefined);
    hooks.setConnection({ invoke, send: vi.fn(), stop, off: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      agents: [],
      selectedAgentId: "examiner"
    });
    await hangUp();
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/v1/sessions/s1",
      expect.objectContaining({
        method: "DELETE",
        headers: expect.any(Headers)
      })
    );
    const deleteHeaders = fetchMock.mock.calls[0][1].headers as Headers;
    expect(deleteHeaders.get("X-AgentCore-Owner-Capability")).toBe("tok");
    expect(useSessionStore.getState().sessionId).toBe("s1");
    expect(useSessionStore.getState().status).toBe("ended");
    expect(useSessionStore.getState().connection).toBe("idle");
  });

  it("keeps the session when EndSession and HTTP end both fail", async () => {
    window.localStorage.setItem("agent-core.owner-capability", "tok");
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue({ ok: false, status: 503 }));
    const invoke = vi.fn().mockResolvedValue({ accepted: false, error: { message: "Persistent save failed." } });
    hooks.setConnection({ invoke, send: vi.fn(), stop: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      agents: [],
      selectedAgentId: "examiner"
    });
    await hangUp();
    expect(useSessionStore.getState().sessionId).toBe("s1");
    expect(useSessionStore.getState().connection).toBe("ready");
    expect(useSessionStore.getState().error).toContain("Unable to end the session");
    expect(useSessionStore.getState().errorFatal).toBe(false);
    expect(useSessionStore.getState().errorHoldSequence).toBe(0);
  });

  it("refreshes owner capability and retries attach once after Unauthorized", async () => {
    const api = await import("./api");
    window.localStorage.setItem(api.OWNER_STORAGE_KEY, "stale-token");
    const clearSpy = vi.spyOn(api, "clearOwnerCapability");
    const ensureSpy = vi.spyOn(api, "ensureOwnerCapability").mockResolvedValue("fresh-token");
    const invoke = vi
      .fn()
      .mockResolvedValueOnce({
        accepted: false,
        error: { code: "Unauthorized", message: "Owner capability is missing or invalid." }
      })
      .mockResolvedValueOnce({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "reconnecting",
      sessionId: "s1",
      attachmentId: null,
      lastServerSequence: 2,
      agents: [],
      selectedAgentId: "examiner"
    });
    const attached = await hooks.attachWithBusyRetry!(2);
    expect(attached).toBe(true);
    expect(invoke).toHaveBeenCalledTimes(2);
    expect(clearSpy).toHaveBeenCalledTimes(1);
    expect(ensureSpy).toHaveBeenCalledTimes(1);
  });

  it("resumes client transcript after interrupted server-audio playback finishes flushing", async () => {
    vi.spyOn(capture, "flushPlayback").mockResolvedValue(120);
    hooks.setConnection({ invoke: vi.fn().mockResolvedValue({ accepted: true }), send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      mode: "voice",
      sttTransport: "clientTranscript",
      ttsTransport: "serverAudio",
      voiceInputHeldForAgentOutput: true,
      liveResponseId: null,
      agents: [],
      selectedAgentId: "examiner"
    });
    hooks.setClientTranscriptHeldForAgent!(true);
    await hooks.interruptPlayback!("r1");
    await vi.waitFor(() => {
      expect(useSessionStore.getState().voiceInputHeldForAgentOutput).toBe(false);
    });
  });

  it("marks the connection failed when automatic reconnect attach is rejected", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: false, error: { message: "Session is attached to another connection." } });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "reconnecting",
      sessionId: "s1",
      attachmentId: null,
      lastServerSequence: 3,
      error: "Protocol error.",
      errorFatal: true,
      agents: [],
      selectedAgentId: "examiner"
    });
    await hooks.attachAfterReconnect();
    expect(invoke).toHaveBeenCalledWith("Attach", expect.objectContaining({ type: "session.attach" }));
    expect(useSessionStore.getState().connection).toBe("failed");
    expect(useSessionStore.getState().error).toContain("another connection");
    expect(useSessionStore.getState().errorFatal).toBe(false);
  });

  it("keeps pending voice cancel when SetMode is rejected", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: false, error: { message: "Session is busy." } });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      pendingMode: "voice",
      preflightReady: true,
      agents: [],
      selectedAgentId: "examiner"
    });
    await cancelVoice();
    expect(useSessionStore.getState().pendingMode).toBe("voice");
    expect(useSessionStore.getState().error).toContain("Session is busy");
    expect(useSessionStore.getState().errorFatal).toBe(false);
    expect(useSessionStore.getState().preflightReady).toBe(false);
  });

  it("does not send voice SetMode after cancel during preflight", async () => {
    let finishPreflight: (() => void) | undefined;
    vi.spyOn(capture, "preflight").mockImplementation(
      () => new Promise<void>((resolve) => {
        finishPreflight = resolve;
      })
    );
    vi.spyOn(capture, "isPrepared").mockReturnValue(false);
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      voiceAvailable: true,
      agents: [],
      selectedAgentId: "examiner"
    });
    const pending = requestVoice();
    await cancelVoice();
    finishPreflight?.();
    await pending;
    expect(invoke.mock.calls.some((call) => call[1]?.payload?.mode === "voice")).toBe(false);
    expect(invoke).toHaveBeenCalledWith("SetMode", expect.objectContaining({ payload: { mode: "text" } }));
  });

  it("clears the voice-request latch when cancel races an in-flight SetMode ACK", async () => {
    let resolveVoiceAck: ((value: { accepted: boolean }) => void) | undefined;
    const invoke = vi.fn().mockImplementation((_method: string, envelope: { payload?: { mode?: string } }) => {
      if (envelope?.payload?.mode === "voice") {
        return new Promise<{ accepted: boolean }>((resolve) => {
          resolveVoiceAck = resolve;
        });
      }

      return Promise.resolve({ accepted: true });
    });
    vi.spyOn(capture, "preflight").mockResolvedValue(undefined);
    vi.spyOn(capture, "isPrepared").mockReturnValue(false);
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      voiceAvailable: true,
      agents: [],
      selectedAgentId: "examiner"
    });

    const pending = requestVoice();
    await vi.waitFor(() => expect(resolveVoiceAck).toBeDefined());
    const cancel = cancelVoice();
    resolveVoiceAck?.({ accepted: true });
    await Promise.all([pending, cancel]);

    const attached = await hooks.attachWithBusyRetry!(null);
    expect(attached).toBe(true);
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "ready",
      sequence: 1,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: null,
      type: "session.ready",
      payload: {
        mode: "voice",
        pendingMode: null,
        status: "attached",
        streamId: "stream-1",
        agent: { name: "Alex", role: "Examiner", voiceAvailable: true },
        history: [],
        capabilities: {
          stt: { transport: "serverAudio" },
          tts: { transport: "serverAudio" }
        }
      }
    });
    expect(useSessionStore.getState().mode).toBe("text");
    expect(useSessionStore.getState().streamId).toBeNull();
  });

  it("preflights serverAudio input without playback worklet for clientSpeech output", async () => {
    const preflight = vi.spyOn(capture, "preflight").mockResolvedValue(undefined);
    vi.spyOn(capture, "isPrepared").mockReturnValue(false);
    window.__agentCoreSpeechTest = { fakeSynthesizer: true };
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      voiceAvailable: true,
      sttTransport: "serverAudio",
      ttsTransport: "clientSpeech",
      agents: [],
      selectedAgentId: "examiner"
    });
    await requestVoice();
    expect(preflight).toHaveBeenCalledWith({ microphone: true, playback: false });
    expect(invoke).toHaveBeenCalledWith("SetMode", expect.objectContaining({ payload: { mode: "voice" } }));
  });

  it("preflights playback worklet without microphone for clientTranscript plus serverAudio", async () => {
    const preflight = vi.spyOn(capture, "preflight").mockResolvedValue(undefined);
    vi.spyOn(capture, "isPrepared").mockReturnValue(false);
    window.__agentCoreSpeechTest = { fakeRecognizer: true };
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      voiceAvailable: true,
      sttTransport: "clientTranscript",
      ttsTransport: "serverAudio",
      agents: [],
      selectedAgentId: "examiner"
    });
    await requestVoice();
    expect(preflight).toHaveBeenCalledWith({ microphone: false, playback: true });
  });

  it("skips capture worklets when both transports are client-side", async () => {
    const preflight = vi.spyOn(capture, "preflight");
    vi.spyOn(capture, "isPrepared").mockReturnValue(false);
    window.__agentCoreSpeechTest = { fakeRecognizer: true, fakeSynthesizer: true };
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      voiceAvailable: true,
      sttTransport: "clientTranscript",
      ttsTransport: "clientSpeech",
      agents: [],
      selectedAgentId: "examiner"
    });
    await requestVoice();
    expect(preflight).not.toHaveBeenCalled();
    expect(invoke).toHaveBeenCalledWith("SetMode", expect.objectContaining({ payload: { mode: "voice" } }));
  });

  it("does not preflight PCM before a new session advertises transports", async () => {
    const preflight = vi.spyOn(capture, "preflight").mockResolvedValue(undefined);
    vi.spyOn(capture, "isPrepared").mockReturnValue(false);
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue({ ok: false, status: 500 }));
    useSessionStore.setState({
      ...emptySession(),
      connection: "idle",
      sessionId: null,
      voiceAvailable: true,
      agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
      selectedAgentId: "examiner"
    });
    await requestVoice();
    expect(preflight).not.toHaveBeenCalled();
  });

  it("marks the connection failed when gap reattach throws", async () => {
    const stop = vi.fn().mockResolvedValue(undefined);
    const start = vi.fn().mockResolvedValue(undefined);
    const invoke = vi.fn().mockRejectedValue(new Error("Hub disconnected."));
    hooks.setConnection({ invoke, send: vi.fn(), stop, start } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      lastServerSequence: 2,
      error: "Protocol error.",
      errorFatal: true,
      agents: [],
      selectedAgentId: "examiner"
    });
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "gap",
      sequence: 4,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: "r-gap",
      type: "agent.response.started",
      payload: { entryId: "e1", entrySequence: 1 }
    });
    await vi.waitFor(() => {
      expect(useSessionStore.getState().connection).toBe("failed");
      expect(useSessionStore.getState().error).toBe("Hub disconnected.");
    });
    expect(useSessionStore.getState().errorFatal).toBe(false);
  });

  it("retries SessionInUse attach until it succeeds", async () => {
    const invoke = vi
      .fn()
      .mockResolvedValueOnce({
        accepted: false,
        error: { code: "SessionInUse", message: "Session is attached to another connection." }
      })
      .mockResolvedValueOnce({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "reconnecting",
      sessionId: "s1",
      attachmentId: null,
      lastServerSequence: 3,
      agents: [],
      selectedAgentId: "examiner"
    });
    await hooks.attachAfterReconnect();
    expect(invoke).toHaveBeenCalledTimes(2);
    expect(useSessionStore.getState().connection).toBe("reconnecting");
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a2",
      eventId: "ready",
      sequence: 4,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: null,
      type: "session.ready",
      payload: {
        mode: "text",
        pendingMode: null,
        status: "attached",
        agent: { name: "Alex", role: "Examiner", voiceAvailable: true },
        history: []
      }
    });
    expect(useSessionStore.getState().connection).toBe("ready");
  });

  it("marks failed after the hub closes", () => {
    useSessionStore.setState({
      ...emptySession(),
      connection: "reconnecting",
      sessionId: "s1",
      agents: [],
      selectedAgentId: "examiner"
    });
    hooks.handleHubClosed();
    expect(useSessionStore.getState().connection).toBe("failed");
    expect(useSessionStore.getState().error).toContain("Retry");
  });

  it("stops SessionInUse retries when the hub closes", async () => {
    vi.useFakeTimers();
    const invoke = vi
      .fn()
      .mockResolvedValueOnce({
        accepted: false,
        error: { code: "SessionInUse", message: "Session is attached to another connection.", retryAfterMs: 1000 }
      })
      .mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "reconnecting",
      sessionId: "s1",
      lastServerSequence: 3,
      agents: [],
      selectedAgentId: "examiner"
    });
    const pending = hooks.attachAfterReconnect();
    await Promise.resolve();
    hooks.handleHubClosed();
    await vi.advanceTimersByTimeAsync(1000);
    await pending;
    expect(invoke).toHaveBeenCalledTimes(1);
    expect(useSessionStore.getState().connection).toBe("failed");
    vi.useRealTimers();
  });

  it("restores pending attachments after ambiguous SendText failure", async () => {
    const invoke = vi.fn().mockRejectedValue(new Error("Hub disconnected."));
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      draft: "Hello",
      pendingAttachments: [
        {
          localId: "local-1",
          displayName: "notes.md",
          contentType: "text/markdown",
          byteSize: 12,
          status: "ready",
          progress: 100,
          attachmentId: "att-1",
          error: null
        }
      ],
      agents: [],
      selectedAgentId: "examiner"
    });
    await sendDraft();
    expect(useSessionStore.getState().pendingAttachments).toHaveLength(1);
    expect(useSessionStore.getState().pendingAttachments[0]?.attachmentId).toBe("att-1");
    expect(useSessionStore.getState().entries[0]?.status).toBe("sending");
    expect(composerSendEnabled()).toBe(true);
  });

  it("keeps send enabled for text-only ambiguous SendText failure", async () => {
    const invoke = vi.fn().mockRejectedValue(new Error("Hub disconnected."));
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      draft: "Hello",
      agents: [],
      selectedAgentId: "examiner"
    });
    await sendDraft();
    expect(useSessionStore.getState().draft).toBe("");
    expect(useSessionStore.getState().entries[0]?.status).toBe("sending");
    expect(composerSendEnabled()).toBe(true);
  });

  it("clears restored attachments when reconnect history proves the turn persisted", async () => {
    const invoke = vi.fn().mockRejectedValue(new Error("Hub disconnected."));
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      draft: "Hello",
      pendingAttachments: [
        {
          localId: "local-1",
          displayName: "notes.md",
          contentType: "text/markdown",
          byteSize: 12,
          status: "ready",
          progress: 100,
          attachmentId: "att-1",
          error: null
        }
      ],
      agents: [],
      selectedAgentId: "examiner"
    });
    await sendDraft();
    expect(useSessionStore.getState().pendingAttachments).toHaveLength(1);
    const eventId = (invoke.mock.calls[0]?.[1] as { eventId?: string })?.eventId;
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a2",
      eventId: "ready",
      sequence: 1,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: null,
      type: "session.ready",
      payload: {
        mode: "text",
        pendingMode: null,
        status: "attached",
        agent: { name: "Alex", role: "Examiner", voiceAvailable: true },
        history: [
          {
            entryId: eventId!,
            sequence: 1,
            sourceEventId: eventId,
            role: "user",
            text: "Hello",
            deliveryMode: "text",
            status: "completed"
          }
        ]
      }
    });
    expect(useSessionStore.getState().pendingAttachments).toHaveLength(0);
    expect(composerSendEnabled()).toBe(false);
  });

  it("retries unacked text with the original eventId after reconnect", async () => {
    const invoke = vi
      .fn()
      .mockRejectedValueOnce(new Error("Hub disconnected."))
      .mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      draft: "Hello",
      agents: [],
      selectedAgentId: "examiner"
    });
    await sendDraft();
    expect(useSessionStore.getState().draft).toBe("");
    expect(useSessionStore.getState().entries[0]?.text).toBe("Hello");
    expect(useSessionStore.getState().entries[0]?.status).toBe("sending");
    const firstId = (invoke.mock.calls[0]?.[1] as { eventId?: string })?.eventId;
    expect(firstId).toBeTruthy();
    useSessionStore.setState({ draft: "" });
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a2",
      eventId: "ready",
      sequence: 1,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: null,
      type: "session.ready",
      payload: {
        mode: "text",
        pendingMode: null,
        status: "attached",
        agent: { name: "Alex", role: "Examiner", voiceAvailable: true },
        history: []
      }
    });
    await vi.waitFor(() => {
      expect(invoke).toHaveBeenCalledTimes(2);
    });
    expect((invoke.mock.calls[1]?.[1] as { eventId?: string })?.eventId).toBe(firstId);
  });

  it("downgrades durable voice to text on passive session.ready", async () => {
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "connecting",
      sessionId: "s1",
      sttTransport: "clientTranscript",
      ttsTransport: "serverAudio",
      agents: [],
      selectedAgentId: "examiner"
    });
    hooks.markPassiveVoiceReadyDowngrade();
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "ready",
      sequence: 1,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: null,
      type: "session.ready",
      payload: {
        mode: "voice",
        pendingMode: null,
        status: "attached",
        streamId: "stream-1",
        agent: { name: "Alex", role: "Examiner", voiceAvailable: true },
        history: [],
        capabilities: {
          stt: { transport: "clientTranscript" },
          tts: { transport: "serverAudio" }
        }
      }
    });
    expect(useSessionStore.getState().mode).toBe("text");
    expect(useSessionStore.getState().streamId).toBeNull();
    await vi.waitFor(() => {
      expect(invoke).toHaveBeenCalledWith("SetMode", expect.objectContaining({ payload: { mode: "text" } }));
    });
  });

  it("clears a restored draft when reconnect history already has the turn", async () => {
    const eventId = "evt-user-1";
    const invoke = vi.fn().mockRejectedValue(new Error("Hub disconnected."));
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      draft: "Hello",
      agents: [],
      selectedAgentId: "examiner"
    });
    await sendDraft();
    expect(useSessionStore.getState().draft).toBe("");
    expect(useSessionStore.getState().entries[0]?.status).toBe("sending");
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a2",
      eventId: "ready",
      sequence: 1,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: null,
      type: "session.ready",
      payload: {
        mode: "text",
        pendingMode: null,
        status: "attached",
        agent: { name: "Alex", role: "Examiner", voiceAvailable: true },
        history: [
          {
            entryId: eventId,
            sequence: 1,
            sourceEventId: (invoke.mock.calls[0]?.[1] as { eventId?: string })?.eventId,
            role: "user",
            text: "Hello",
            deliveryMode: "text",
            status: "completed"
          }
        ]
      }
    });
    expect(useSessionStore.getState().draft).toBe("");
    expect(invoke).toHaveBeenCalledTimes(1);
  });

  it("clears captureLive before recovering from a sequence gap", async () => {
    const stop = vi.fn().mockResolvedValue(undefined);
    const start = vi.fn().mockResolvedValue(undefined);
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn(), stop, start } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      lastServerSequence: 2,
      mode: "voice",
      captureLive: true,
      agents: [],
      selectedAgentId: "examiner"
    });
    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "gap",
      sequence: 4,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: "r-gap",
      type: "agent.response.started",
      payload: { entryId: "e1", entrySequence: 1 }
    });
    expect(useSessionStore.getState().captureLive).toBe(false);
    await vi.waitFor(() => {
      expect(invoke).toHaveBeenCalled();
    });
  });

  it("replaces session.ready bootstrap with one newest history page", async () => {
    const makeHistoryRow = (sequence: number) => ({
      entryId: `e${sequence}`,
      sequence,
      sourceEventId: `e${sequence}`,
      role: sequence % 2 === 1 ? "user" : "assistant",
      text: `Message ${sequence}`,
      responseId: sequence % 2 === 0 ? `r${sequence}` : null,
      status: "completed",
      deliveryMode: "text",
      heardTextEndExclusive: 8,
      receivedTextEndExclusive: 8,
      createdAt: "2026-01-01T00:00:00Z"
    });
    const bootstrap = Array.from({ length: 20 }, (_, index) => makeHistoryRow(index + 431));
    const newest = Array.from({ length: 50 }, (_, index) => makeHistoryRow(index + 451));
    vi.mocked(listSessionMessages).mockResolvedValue({
      items: newest,
      nextAfter: 500,
      hasMore: false,
      hasOlder: true,
      nextBefore: 451
    });
    hooks.setConnection({ invoke: vi.fn(), send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "connecting",
      sessionId: "s-active",
      attachmentId: "a1",
      lastServerSequence: 0,
      agents: [],
      selectedAgentId: "examiner"
    });

    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s-active",
      attachmentId: "a1",
      eventId: "ready",
      sequence: 1,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: null,
      type: "session.ready",
      payload: {
        mode: "text",
        pendingMode: null,
        status: "attached",
        agent: { name: "Alex", role: "Examiner", voiceAvailable: true },
        history: bootstrap
      }
    });

    await vi.waitFor(() => {
      expect(useSessionStore.getState().entries).toHaveLength(50);
    });
    expect(listSessionMessages).toHaveBeenCalledTimes(1);
    expect(listSessionMessages).toHaveBeenCalledWith(
      "s-active",
      expect.objectContaining({ limit: 50 })
    );
    expect(vi.mocked(listSessionMessages).mock.calls[0]?.[1]).not.toHaveProperty("after");
    expect(useSessionStore.getState().entries[0]?.text).toBe("Message 451");
    expect(useSessionStore.getState().entries[49]?.text).toBe("Message 500");
    expect(useSessionStore.getState().historyHasOlder).toBe(true);
  });

  it("does not fetch additional pages when bootstrap starts above sequence fifty", async () => {
    const makeHistoryRow = (sequence: number) => ({
      entryId: `e${sequence}`,
      sequence,
      sourceEventId: `e${sequence}`,
      role: sequence % 2 === 1 ? "user" : "assistant",
      text: `Message ${sequence}`,
      responseId: sequence % 2 === 0 ? `r${sequence}` : null,
      status: "completed",
      deliveryMode: "text",
      heardTextEndExclusive: 8,
      receivedTextEndExclusive: 8,
      createdAt: "2026-01-01T00:00:00Z"
    });
    const bootstrap = Array.from({ length: 50 }, (_, index) => makeHistoryRow(index + 101));
    const newest = Array.from({ length: 50 }, (_, index) => makeHistoryRow(index + 101));
    vi.mocked(listSessionMessages).mockResolvedValue({
      items: newest,
      nextAfter: 150,
      hasMore: false,
      hasOlder: true,
      nextBefore: 101
    });
    hooks.setConnection({ invoke: vi.fn(), send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "connecting",
      sessionId: "s-long",
      attachmentId: "a1",
      lastServerSequence: 0,
      agents: [],
      selectedAgentId: "examiner"
    });

    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s-long",
      attachmentId: "a1",
      eventId: "ready",
      sequence: 1,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: null,
      type: "session.ready",
      payload: {
        mode: "text",
        pendingMode: null,
        status: "attached",
        agent: { name: "Alex", role: "Examiner", voiceAvailable: true },
        history: bootstrap
      }
    });

    await vi.waitFor(() => {
      expect(listSessionMessages).toHaveBeenCalledTimes(1);
    });
    expect(useSessionStore.getState().entries).toHaveLength(50);
    expect(useSessionStore.getState().entries[0]?.text).toBe("Message 101");
    expect(useSessionStore.getState().entries[49]?.text).toBe("Message 150");
  });

  it("surfaces start conversation failures", async () => {
    useSessionStore.setState({
      ...emptySession(),
      agents: [
        {
          id: "examiner",
          version: 1,
          name: "Alex",
          role: "Examiner",
          description: "",
          voiceAvailable: true,
          language: "en"
        }
      ],
      chatAgentInstances: [],
      chatAgentInstancesLoading: false,
      chatAgentInstancesError: null,
      newChatIdentityKey: "legacy:examiner",
      selectedAgentId: "examiner"
    });
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: RequestInfo) => {
        const url = String(input);
        if (url.includes("owner-capability")) {
          return { ok: true, json: async () => ({ token: "t" }) };
        }
        return { ok: false, status: 503 };
      })
    );
    await startConversation();
    expect(useSessionStore.getState().sessionId).toBeNull();
    expect(useSessionStore.getState().error).toContain("Unable to create a session");
    expect(useSessionStore.getState().errorFatal).toBe(false);
  });
});
