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
});
