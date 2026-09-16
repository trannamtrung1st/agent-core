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
    await capture.start({
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
    outputPort.onmessage?.({ data: { type: "flushed", consumed: 256, responseId: "r1", epoch: 0 } } as MessageEvent);
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

  it("start throws when resources are not prepared", async () => {
    await expect(
      capture.start({
        sendAudio: vi.fn(),
        speechStarted: vi.fn(),
        speechEnded: vi.fn()
      })
    ).rejects.toThrow(/not prepared/);
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
    await capture.start({
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
    await capture.start({
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
    await capture.start({
      sendAudio,
      speechStarted: vi.fn(),
      speechEnded: vi.fn()
    });
    const pcm = new ArrayBuffer(960);
    const pending = inputPort.onmessage?.({ data: { pcm, sampleOffset: 0 } } as MessageEvent);
    await capture.muteInput();
    await capture.start({
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

  it("preserves the pre-reset sample offset for speechEnded on mute", async () => {
    const stop = vi.fn();
    const speechEnded = vi.fn();
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
    await capture.start({
      sendAudio: vi.fn(),
      speechStarted: vi.fn(),
      speechEnded
    });
    const pcm = new ArrayBuffer(960);
    const view = new DataView(pcm);
    for (let index = 0; index < 480; index += 1) {
      view.setInt16(index * 2, 0x7fff, true);
    }
    for (let index = 0; index < 20; index += 1) {
      await inputPort.onmessage?.({ data: { pcm, sampleOffset: index * 480 } } as MessageEvent);
    }
    await capture.muteInput();
    expect(speechEnded).toHaveBeenCalled();
    expect(speechEnded.mock.calls[0]?.[1]).toBe(9600);
  });

  it("speechEnded on mute uses transmitted offset, not dropped queued frames", async () => {
    const stop = vi.fn();
    const speechEnded = vi.fn();
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
    await capture.start({
      sendAudio: vi.fn(),
      speechStarted: vi.fn(),
      speechEnded
    });
    const pcm = new ArrayBuffer(960);
    const view = new DataView(pcm);
    for (let index = 0; index < 480; index += 1) {
      view.setInt16(index * 2, 0x7fff, true);
    }
    for (let index = 0; index < 5; index += 1) {
      await inputPort.onmessage?.({ data: { pcm, sampleOffset: index * 480 } } as MessageEvent);
    }
    capture.outgoingHold = () => hold;
    const pending = inputPort.onmessage?.({ data: { pcm, sampleOffset: 2400 } } as MessageEvent);
    await capture.muteInput();
    capture.outgoingHold = null;
    release?.();
    await pending;
    expect(speechEnded).toHaveBeenCalledWith(expect.any(String), 2400, 0, true);
  });

  it("start while an utterance is open ends it on the previous hooks", async () => {
    const stop = vi.fn();
    const firstEnded = vi.fn();
    const secondEnded = vi.fn();
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
    await capture.start({
      sendAudio: vi.fn(),
      speechStarted: vi.fn(),
      speechEnded: firstEnded
    });
    const pcm = new ArrayBuffer(960);
    const view = new DataView(pcm);
    for (let index = 0; index < 480; index += 1) {
      view.setInt16(index * 2, 0x7fff, true);
    }
    for (let index = 0; index < 20; index += 1) {
      await inputPort.onmessage?.({ data: { pcm, sampleOffset: index * 480 } } as MessageEvent);
    }
    await capture.start({
      sendAudio: vi.fn(),
      speechStarted: vi.fn(),
      speechEnded: secondEnded
    });
    expect(firstEnded).toHaveBeenCalledWith(expect.any(String), 9600, 0, true);
    expect(secondEnded).not.toHaveBeenCalled();
  });

  it("restarting capture while a send is in flight does not mutate the new generation", async () => {
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
    await capture.start({
      sendAudio,
      speechStarted: vi.fn(),
      speechEnded: vi.fn()
    });
    const pcm = new ArrayBuffer(960);
    const pending = inputPort.onmessage?.({ data: { pcm, sampleOffset: 0 } } as MessageEvent);
    await capture.start({
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

  it("drops an in-flight send after enqueueOutbound when start rotates generation", async () => {
    const stop = vi.fn();
    let release: ((value: void) => void) | undefined;
    const hold = new Promise<void>((resolve) => {
      release = resolve;
    });
    const sendAudio = vi.fn().mockImplementation(() => hold);
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
    await capture.start({
      sendAudio,
      speechStarted: vi.fn(),
      speechEnded: vi.fn()
    });
    const pcm = new ArrayBuffer(960);
    const pending = inputPort.onmessage?.({ data: { pcm, sampleOffset: 0 } } as MessageEvent);
    await vi.waitFor(() => expect(sendAudio).toHaveBeenCalledTimes(1));
    const restart = capture.start({
      sendAudio,
      speechStarted: vi.fn(),
      speechEnded: vi.fn()
    });
    release?.();
    await pending;
    await restart;
    await inputPort.onmessage?.({ data: { pcm, sampleOffset: 0 } } as MessageEvent);
    expect(sendAudio).toHaveBeenCalledTimes(2);
    expect(sendAudio.mock.calls[1]?.[0]?.frameSequence).toBe(1);
  });
});
