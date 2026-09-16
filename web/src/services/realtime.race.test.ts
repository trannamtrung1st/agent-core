import { afterEach, describe, expect, it, vi } from "vitest";
import { capture } from "../audio/capture";
import { encodePcm16Le } from "../audio/pcm";
import { emptySession, useSessionStore, type ServerEvent } from "../state/sessionStore";
import { realtimeTestHooks, requestVoice, sendDraft, setMuted, hangUp, cancelVoice, startConversation } from "./realtime";

const hooks = realtimeTestHooks!;

function pcmFrame(samples = 480): Uint8Array {
  return encodePcm16Le(new Float32Array(samples).fill(0.1));
}

describe("realtime race handling", () => {
  afterEach(() => {
    capture.release();
    hooks.resetOutput();
    hooks.setConnection(null);
    useSessionStore.setState({
      ...emptySession(),
      agents: [],
      selectedAgentId: "examiner"
    });
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
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
    expect(useSessionStore.getState().error).toContain("Control sequence gap");
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
    resolveAck({ accepted: true });
    await Promise.all([first, second]);
    expect(invoke).toHaveBeenCalledTimes(1);
    expect(useSessionStore.getState().entries).toHaveLength(1);
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
    const fetchMock = vi.fn().mockResolvedValue({ ok: true });
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
    expect(fetchMock).toHaveBeenCalledWith("/api/v1/sessions/s1", { method: "DELETE" });
    expect(useSessionStore.getState().sessionId).toBeNull();
  });

  it("keeps the session when EndSession and HTTP end both fail", async () => {
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
    expect(useSessionStore.getState().draft).toBe("Hello");
    const firstId = (invoke.mock.calls[0]?.[1] as { eventId?: string })?.eventId;
    expect(firstId).toBeTruthy();
    useSessionStore.setState({ draft: "Hello" });
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
    expect(useSessionStore.getState().draft).toBe("Hello");
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

  it("surfaces start conversation failures", async () => {
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
