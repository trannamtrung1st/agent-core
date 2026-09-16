import { afterEach, describe, expect, it, vi } from "vitest";
import { capture, type CaptureHooks } from "../audio/capture";
import { encodePcm16Le } from "../audio/pcm";
import { MAX_QUEUED_SAMPLES } from "../audio/outputAdmission";
import { emptySession, useSessionStore, type ServerEvent } from "../state/sessionStore";
import { realtimeTestHooks } from "./realtime";

const hooks = realtimeTestHooks!;

function pcmFrame(samples = 480): Uint8Array {
  return encodePcm16Le(new Float32Array(samples).fill(0.1));
}

function startedEvent(responseId: string): ServerEvent {
  return {
    protocolVersion: 1,
    sessionId: "s1",
    attachmentId: "a1",
    eventId: "e1",
    sequence: 1,
    timestamp: "2026-09-15T00:00:00.000Z",
    correlationId: "c1",
    causationId: null,
    responseId,
    type: "agent.response.started",
    payload: {}
  };
}

describe("realtime overflow and capture restart", () => {
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
  });

  it("overflow flushes playback and then plays a later response", async () => {
    const flush = vi.spyOn(capture, "flushPlayback").mockResolvedValue(240);
    const enqueue = vi.spyOn(capture, "enqueuePlayback").mockReturnValue(true);
    vi.spyOn(capture, "playbackQueued").mockReturnValue(0);
    vi.spyOn(capture, "playbackConsumed").mockReturnValue(0);
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

    hooks.markOutputStarted("r1");
    hooks.handleAudioOutput({
      sessionId: "s1",
      attachmentId: "a1",
      responseId: "r1",
      frameSequence: 1,
      sampleOffset: 0,
      data: pcmFrame(),
      isFinal: false
    });
    expect(enqueue).toHaveBeenCalledWith("r1", expect.any(Uint8Array), false);

    enqueue.mockReturnValueOnce(false);
    hooks.handleAudioOutput({
      sessionId: "s1",
      attachmentId: "a1",
      responseId: "r1",
      frameSequence: 2,
      sampleOffset: 480,
      data: pcmFrame(),
      isFinal: false
    });
    await vi.waitFor(() => expect(flush).toHaveBeenCalledWith("r1"));
    await vi.waitFor(() =>
      expect(invoke).toHaveBeenCalledWith(
        "PlaybackStopped",
        expect.objectContaining({ type: "playback.stopped" })
      )
    );

    enqueue.mockReturnValue(true);
    hooks.markOutputStarted("r2");
    hooks.handleAudioOutput({
      sessionId: "s1",
      attachmentId: "a1",
      responseId: "r2",
      frameSequence: 1,
      sampleOffset: 0,
      data: pcmFrame(),
      isFinal: true
    });
    expect(enqueue).toHaveBeenCalledWith("r2", expect.any(Uint8Array), true);
    expect(useSessionStore.getState().error).toContain("2 second queue");
    expect(useSessionStore.getState().errorFatal).toBe(false);
    expect(useSessionStore.getState().errorHoldSequence).toBe(0);
    void MAX_QUEUED_SAMPLES;
  });

  it("keeps the overflow banner across the next state change", async () => {
    vi.spyOn(capture, "enqueuePlayback").mockReturnValue(false);
    vi.spyOn(capture, "flushPlayback").mockResolvedValue(0);
    hooks.setConnection({ invoke: vi.fn().mockResolvedValue({ accepted: true }), send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      lastServerSequence: 4,
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
      isFinal: false
    });
    expect(useSessionStore.getState().error).toContain("2 second queue");
    expect(useSessionStore.getState().errorHoldSequence).toBe(4);

    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "s5",
      sequence: 5,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: null,
      type: "session.state.changed",
      payload: {
        status: "attached",
        mode: "voice",
        pendingMode: null,
        muted: false,
        inputState: "listening",
        outputState: "idle"
      }
    });
    expect(useSessionStore.getState().error).toContain("2 second queue");

    hooks.handleEvent({
      protocolVersion: 1,
      sessionId: "s1",
      attachmentId: "a1",
      eventId: "s6",
      sequence: 6,
      timestamp: "2026-09-15T00:00:00.000Z",
      correlationId: "c1",
      causationId: null,
      responseId: null,
      type: "session.state.changed",
      payload: {
        status: "attached",
        mode: "voice",
        pendingMode: null,
        muted: false,
        inputState: "userSpeaking",
        outputState: "idle"
      }
    });
    expect(useSessionStore.getState().error).toBeNull();
  });

  it("stops flushing early audio after overflow", async () => {
    const enqueue = vi.spyOn(capture, "enqueuePlayback").mockReturnValue(true);
    vi.spyOn(capture, "flushPlayback").mockResolvedValue(0);
    let queuedSamples = 0;
    vi.spyOn(capture, "playbackQueued").mockImplementation(() => queuedSamples);
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
      responseId: "r1",
      frameSequence: 1,
      sampleOffset: 0,
      data: pcmFrame(),
      isFinal: false
    });
    hooks.handleAudioOutput({
      sessionId: "s1",
      attachmentId: "a1",
      responseId: "r1",
      frameSequence: 2,
      sampleOffset: 480,
      data: pcmFrame(),
      isFinal: false
    });
    queuedSamples = MAX_QUEUED_SAMPLES;
    hooks.handleEvent(startedEvent("r1"));
    await vi.waitFor(() => expect(useSessionStore.getState().error).toContain("2 second queue"));
    expect(enqueue).not.toHaveBeenCalled();
  });

  it("restarts capture hooks when the server rotates streamId", () => {
    const start = vi.spyOn(capture, "start").mockResolvedValue(undefined);
    vi.spyOn(capture, "isPrepared").mockReturnValue(true);
    const streaming = vi.spyOn(capture, "isStreaming");
    hooks.setConnection({ invoke: vi.fn(), send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      mode: "voice",
      streamId: "stream-1",
      agents: [],
      selectedAgentId: "examiner"
    });
    streaming.mockReturnValue(false);
    hooks.syncCapture();
    expect(start).toHaveBeenCalledTimes(1);

    streaming.mockReturnValue(true);
    hooks.syncCapture();
    expect(start).toHaveBeenCalledTimes(1);

    useSessionStore.setState({ streamId: "stream-2" });
    hooks.syncCapture();
    expect(start).toHaveBeenCalledTimes(2);
  });

  it("drops in-flight speech boundaries after streamId rotates", async () => {
    const generations: CaptureHooks[] = [];
    vi.spyOn(capture, "start").mockImplementation(async (captureHooks) => {
      generations.push(captureHooks);
    });
    vi.spyOn(capture, "isPrepared").mockReturnValue(true);
    const streaming = vi.spyOn(capture, "isStreaming");
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      mode: "voice",
      streamId: "stream-1",
      agents: [],
      selectedAgentId: "examiner"
    });
    streaming.mockReturnValue(false);
    hooks.syncCapture();
    expect(generations).toHaveLength(1);

    await generations[0]!.speechStarted("u1", 0, 0.9);
    expect(invoke).toHaveBeenCalledWith(
      "SpeechStarted",
      expect.objectContaining({ payload: expect.objectContaining({ streamId: "stream-1" }) })
    );

    streaming.mockReturnValue(true);
    useSessionStore.setState({ streamId: "stream-2" });
    hooks.syncCapture();
    expect(generations).toHaveLength(2);
    invoke.mockClear();

    await generations[0]!.speechStarted("u1", 480, 0.9);
    await generations[0]!.speechEnded("u1", 960, 0.2);
    expect(invoke).not.toHaveBeenCalled();

    await generations[0]!.speechEnded("u1", 960, 0.2, true);
    expect(invoke).not.toHaveBeenCalled();
  });

  it("closes an open utterance through capture.start when streamId rotates", async () => {
    const inputPort = stubCaptureGraph();
    const invoke = vi.fn().mockResolvedValue({ accepted: true });
    hooks.setConnection({ invoke, send: vi.fn() } as never);
    useSessionStore.setState({
      ...emptySession(),
      connection: "ready",
      sessionId: "s1",
      attachmentId: "a1",
      mode: "voice",
      streamId: "stream-1",
      agents: [],
      selectedAgentId: "examiner"
    });
    await capture.preflight();
    hooks.syncCapture();
    await vi.waitFor(() => expect(capture.isStreaming()).toBe(true));

    const pcm = loudPcmFrame();
    for (let index = 0; index < 8; index += 1) {
      await inputPort.onmessage?.({ data: { pcm, sampleOffset: index * 480 } } as MessageEvent);
    }
    await vi.waitFor(() =>
      expect(invoke).toHaveBeenCalledWith(
        "SpeechStarted",
        expect.objectContaining({ payload: expect.objectContaining({ streamId: "stream-1" }) })
      )
    );
    invoke.mockClear();

    useSessionStore.setState({ streamId: "stream-2" });
    hooks.syncCapture();
    await vi.waitFor(() => expect(capture.isStreaming()).toBe(true));
    expect(invoke).not.toHaveBeenCalledWith(
      "SpeechEnded",
      expect.objectContaining({ payload: expect.objectContaining({ streamId: "stream-1" }) })
    );

    invoke.mockClear();
    for (let index = 0; index < 20; index += 1) {
      await inputPort.onmessage?.({ data: { pcm, sampleOffset: index * 480 } } as MessageEvent);
    }
    await vi.waitFor(() =>
      expect(invoke).toHaveBeenCalledWith(
        "SpeechStarted",
        expect.objectContaining({ payload: expect.objectContaining({ streamId: "stream-2" }) })
      )
    );
    expect(invoke).not.toHaveBeenCalledWith(
      "SpeechStarted",
      expect.objectContaining({ payload: expect.objectContaining({ streamId: "stream-1" }) })
    );
  });
});

function loudPcmFrame(): ArrayBuffer {
  const pcm = new ArrayBuffer(960);
  const view = new DataView(pcm);
  for (let index = 0; index < 480; index += 1) {
    view.setInt16(index * 2, 0x7fff, true);
  }
  return pcm;
}

function stubCaptureGraph(): { onmessage: ((event: MessageEvent) => void) | null } {
  vi.stubGlobal("navigator", {
    mediaDevices: {
      getUserMedia: vi.fn().mockResolvedValue({
        getTracks: () => [{ stop: vi.fn() }]
      })
    }
  });
  const inputPort = { onmessage: null as ((event: MessageEvent) => void) | null, postMessage: vi.fn() };
  const outputPort = { onmessage: null as ((event: MessageEvent) => void) | null, postMessage: vi.fn() };
  let created = 0;
  vi.stubGlobal("AudioContext", class {
    state = "running";
    destination = {};
    resume = vi.fn();
    close = vi.fn();
    audioWorklet = { addModule: vi.fn().mockResolvedValue(undefined) };
    createGain = () => ({ gain: { value: 0 }, connect: vi.fn(), disconnect: vi.fn() });
    createMediaStreamSource = () => ({ connect: vi.fn(), disconnect: vi.fn() });
  });
  vi.stubGlobal("AudioWorkletNode", class {
    port = created++ === 0 ? inputPort : outputPort;
    connect = vi.fn();
    disconnect = vi.fn();
  });
  return inputPort;
}
