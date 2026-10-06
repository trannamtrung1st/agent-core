import { waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { OWNER_STORAGE_KEY } from "./api";

beforeEach(() => { vi.resetModules(); window.localStorage.setItem(OWNER_STORAGE_KEY, "owner-token"); });
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.useRealTimers(); window.localStorage.clear(); });
const record = (sessionId = "s1", artifactId = "a1") => ({ sessionId, artifactId,
  displayName: "canonical.md", contentType: "text/markdown", byteSize: 10 });
const response = (value = record()) => ({ ok: true, status: 200, json: async () => value });

describe("artifact HTTP", () => {
  it("deduplicates authenticated metadata, scopes by session, and never reads a body on render", async () => {
    const fetchMock = vi.fn().mockImplementation(async (url: string) => response(record(url.includes("s2") ? "s2" : "s1")));
    vi.stubGlobal("fetch", fetchMock);
    const { getArtifact } = await import("./artifacts");
    const first = getArtifact("s1", "a1");
    expect(getArtifact("s1", "a1")).toBe(first);
    expect(await first).toEqual(record());
    await getArtifact("s1", "a1");
    await getArtifact("s2", "a1");
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls[0][0]).toBe("/api/v2/sessions/s1/artifacts/a1");
    expect((fetchMock.mock.calls[0][1].headers as Headers).get("X-AgentCore-Owner-Capability")).toBe("owner-token");
    expect(fetchMock.mock.calls.every(([url]) => !url.endsWith("/content"))).toBe(true);
  });

  it("evicts least recently used metadata and bounds pending requests", async () => {
    const fetchMock = vi.fn().mockImplementation(async (url: string) => response(record("s1", url.split("/").pop())));
    vi.stubGlobal("fetch", fetchMock);
    const { getArtifact } = await import("./artifacts");
    for (let i = 0; i < 128; i++) await getArtifact("s1", `a${i}`);
    await getArtifact("s1", "a0");
    await getArtifact("s1", "a128");
    await getArtifact("s1", "a0");
    expect(fetchMock).toHaveBeenCalledTimes(129);
    await getArtifact("s1", "a1");
    expect(fetchMock).toHaveBeenCalledTimes(130);
    // Unresolved requests are also evicted; they cannot grow a second global map.
    fetchMock.mockImplementation(() => new Promise(() => {}));
    for (let i = 0; i < 129; i++) void getArtifact("s2", `a${i}`);
    void getArtifact("s2", "a0");
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(260));
  });

  it("failed or foreign metadata is not cached and supports retry", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce({ ok: false, status: 404 })
      .mockResolvedValueOnce(response(record("foreign"))).mockResolvedValue(response());
    vi.stubGlobal("fetch", fetchMock);
    const { getArtifact } = await import("./artifacts");
    await expect(getArtifact("s1", "a1")).rejects.toThrow("File unavailable");
    await expect(getArtifact("s1", "a1")).rejects.toThrow("File unavailable");
    expect(await getArtifact("s1", "a1")).toEqual(record());
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  it("downloads fresh authenticated bytes only on activation, names the file canonically, and disposes URLs", async () => {
    vi.useFakeTimers();
    const blob = new Blob(["exact bytes"]);
    const fetchMock = vi.fn().mockResolvedValueOnce(response()).mockResolvedValue({ ok: true, status: 200, blob: async () => blob });
    vi.stubGlobal("fetch", fetchMock);
    const create = vi.fn().mockReturnValue("blob:temporary");
    const revoke = vi.fn();
    Object.defineProperty(URL, "createObjectURL", { configurable: true, value: create });
    Object.defineProperty(URL, "revokeObjectURL", { configurable: true, value: revoke });
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
      expect(this.download).toBe("canonical.md");
      expect(this.href).toBe("blob:temporary");
      expect(this.isConnected).toBe(true);
    });
    const { getArtifact, downloadArtifact } = await import("./artifacts");
    await getArtifact("s1", "a1");
    expect(create).not.toHaveBeenCalled();
    await downloadArtifact("s1", "a1");
    await downloadArtifact("s1", "a1");
    expect(fetchMock.mock.calls.filter(([url]) => url.endsWith("/content"))).toHaveLength(2);
    expect(click).toHaveBeenCalledTimes(2);
    expect(document.querySelector("a[download]")).toBeNull();
    expect(create).toHaveBeenCalledWith(blob);
    await vi.runAllTimersAsync();
    expect(revoke).toHaveBeenCalledTimes(2);
  });

  it("retries a body failure and does not launch cancelled downloads", async () => {
    const create = vi.fn().mockReturnValue("blob:test");
    Object.defineProperty(URL, "createObjectURL", { configurable: true, value: create });
    Object.defineProperty(URL, "revokeObjectURL", { configurable: true, value: vi.fn() });
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
    vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(response())
      .mockResolvedValueOnce({ ok: false, status: 500 })
      .mockResolvedValue({ ok: true, status: 200, blob: async () => new Blob(["bytes"]) }));
    const { downloadArtifact } = await import("./artifacts");
    await expect(downloadArtifact("s1", "a1")).rejects.toThrow("Download failed");
    await downloadArtifact("s1", "a1");
    const controller = new AbortController(); controller.abort();
    await expect(downloadArtifact("s1", "a1", controller.signal)).rejects.toThrow();
    expect(create).toHaveBeenCalledTimes(1);
  });

  it("discards bytes that finish loading after cancellation", async () => {
    let finishBody!: (value: Blob) => void;
    const body = vi.fn(() => new Promise<Blob>(resolve => { finishBody = resolve; }));
    const create = vi.fn();
    Object.defineProperty(URL, "createObjectURL", { configurable: true, value: create });
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
    vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(response())
      .mockResolvedValueOnce({ ok: true, status: 200, blob: body }));
    const { downloadArtifact } = await import("./artifacts");
    const controller = new AbortController();
    const pending = downloadArtifact("s1", "a1", controller.signal);
    const rejected = expect(pending).rejects.toThrow();
    await waitFor(() => expect(body).toHaveBeenCalledOnce());
    controller.abort();
    finishBody(new Blob(["late bytes"]));
    await rejected;
    expect(create).not.toHaveBeenCalled();
    expect(click).not.toHaveBeenCalled();
  });
});
