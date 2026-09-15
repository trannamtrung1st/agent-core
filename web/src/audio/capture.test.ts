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
});
