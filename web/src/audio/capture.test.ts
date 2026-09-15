import { afterEach, describe, expect, it, vi } from "vitest";
import { capture } from "./capture";

describe("capture preflight", () => {
  afterEach(() => {
    capture.release();
    vi.unstubAllGlobals();
  });

  it("fails closed when getUserMedia rejects and releases tracks", async () => {
    const stop = vi.fn();
    vi.stubGlobal("navigator", {
      mediaDevices: {
        getUserMedia: vi.fn().mockRejectedValue(new DOMException("denied", "NotAllowedError"))
      }
    });
    await expect(capture.preflight()).rejects.toThrow();
    expect(capture.isPrepared()).toBe(false);
    expect(stop).not.toHaveBeenCalled();
  });

  it("fails when AudioWorklet is missing and stops tracks", async () => {
    const stop = vi.fn();
    vi.stubGlobal("navigator", {
      mediaDevices: {
        getUserMedia: vi.fn().mockResolvedValue({
          getTracks: () => [{ stop }]
        })
      }
    });
    vi.stubGlobal("AudioWorkletNode", undefined);
    await expect(capture.preflight()).rejects.toThrow(/AudioWorklet/);
    expect(capture.isPrepared()).toBe(false);
  });

  it("muteInput stops streaming without releasing the graph", async () => {
    const stop = vi.fn();
    vi.stubGlobal("navigator", {
      mediaDevices: {
        getUserMedia: vi.fn().mockResolvedValue({
          getTracks: () => [{ stop }]
        })
      }
    });
    const port = { onmessage: null as ((event: MessageEvent) => void) | null, postMessage: vi.fn() };
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
      port = port;
      connect = vi.fn();
      disconnect = vi.fn();
    });
    await capture.preflight();
    capture.start({
      sendAudio: vi.fn(),
      speechStarted: vi.fn(),
      speechEnded: vi.fn()
    });
    expect(capture.isStreaming()).toBe(true);
    await capture.muteInput();
    expect(capture.isStreaming()).toBe(false);
    expect(capture.isPrepared()).toBe(true);
    expect(stop).not.toHaveBeenCalled();
  });

  it("flushPlayback uses the flushed acknowledgement, not a pre-request snapshot", async () => {
    const stop = vi.fn();
    vi.stubGlobal("navigator", {
      mediaDevices: {
        getUserMedia: vi.fn().mockResolvedValue({
          getTracks: () => [{ stop }]
        })
      }
    });
    const outputPort = {
      onmessage: null as ((event: MessageEvent) => void) | null,
      postMessage: vi.fn()
    };
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
      port = outputPort;
      connect = vi.fn();
      disconnect = vi.fn();
    });
    await capture.preflight();
    outputPort.onmessage?.({ data: { consumed: 128 } } as MessageEvent);
    expect(capture.playbackConsumed()).toBe(128);
    const pending = capture.flushPlayback("r1");
    outputPort.onmessage?.({ data: { consumed: 256 } } as MessageEvent);
    outputPort.onmessage?.({ data: { type: "flushed", consumed: 256 } } as MessageEvent);
    await expect(pending).resolves.toBe(256);
    expect(capture.playbackConsumed()).toBe(256);
  });

  it("release resolves a pending flush waiter", async () => {
    const stop = vi.fn();
    vi.stubGlobal("navigator", {
      mediaDevices: {
        getUserMedia: vi.fn().mockResolvedValue({
          getTracks: () => [{ stop }]
        })
      }
    });
    const outputPort = {
      onmessage: null as ((event: MessageEvent) => void) | null,
      postMessage: vi.fn()
    };
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
      port = outputPort;
      connect = vi.fn();
      disconnect = vi.fn();
    });
    await capture.preflight();
    outputPort.onmessage?.({ data: { consumed: 240 } } as MessageEvent);
    const pending = capture.flushPlayback("r1");
    capture.release();
    await expect(pending).resolves.toBe(240);
  });

  it("start throws when resources are not prepared", () => {
    expect(() =>
      capture.start({
        sendAudio: vi.fn(),
        speechStarted: vi.fn(),
        speechEnded: vi.fn()
      })
    ).toThrow(/not prepared/);
  });

  it("mute then unmute resets frame sequence so pre-mute audio is dropped", async () => {
    const stop = vi.fn();
    const sendAudio = vi.fn();
    vi.stubGlobal("navigator", {
      mediaDevices: {
        getUserMedia: vi.fn().mockResolvedValue({
          getTracks: () => [{ stop }]
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
    await capture.preflight();
    capture.start({
      sendAudio,
      speechStarted: vi.fn(),
      speechEnded: vi.fn()
    });
    const pcm = new ArrayBuffer(960);
    await inputPort.onmessage?.({ data: { pcm, sampleOffset: 0 } } as MessageEvent);
    expect(sendAudio).toHaveBeenCalledTimes(1);
    await capture.muteInput();
    expect(inputPort.postMessage).toHaveBeenCalledWith({ type: "pause" });
    expect(inputPort.postMessage).toHaveBeenCalledWith({ type: "reset" });
    await inputPort.onmessage?.({ data: { pcm, sampleOffset: 480 } } as MessageEvent);
    expect(sendAudio).toHaveBeenCalledTimes(1);
    capture.start({
      sendAudio,
      speechStarted: vi.fn(),
      speechEnded: vi.fn()
    });
    await inputPort.onmessage?.({ data: { pcm, sampleOffset: 0 } } as MessageEvent);
    expect(sendAudio).toHaveBeenCalledTimes(2);
    expect(sendAudio.mock.calls[1]?.[0]?.frameSequence).toBe(1);
  });

  it("drops a gated in-flight frame across mute and unmute", async () => {
    const stop = vi.fn();
    const sendAudio = vi.fn();
    let release: ((value: void) => void) | undefined;
    const hold = new Promise<void>((resolve) => {
      release = resolve;
    });
    vi.stubGlobal("navigator", {
      mediaDevices: {
        getUserMedia: vi.fn().mockResolvedValue({
          getTracks: () => [{ stop }]
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
    await capture.preflight();
    capture.outgoingHold = () => hold;
    capture.start({
      sendAudio,
      speechStarted: vi.fn(),
      speechEnded: vi.fn()
    });
    const pcm = new ArrayBuffer(960);
    const pending = inputPort.onmessage?.({ data: { pcm, sampleOffset: 0 } } as MessageEvent);
    await capture.muteInput();
    capture.start({
      sendAudio,
      speechStarted: vi.fn(),
      speechEnded: vi.fn()
    });
    capture.outgoingHold = null;
    release?.();
    await pending;
    expect(sendAudio).not.toHaveBeenCalled();
    await inputPort.onmessage?.({ data: { pcm, sampleOffset: 0 } } as MessageEvent);
    expect(sendAudio).toHaveBeenCalledTimes(1);
    expect(sendAudio.mock.calls[0]?.[0]?.frameSequence).toBe(1);
  });
});
