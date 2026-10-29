import { waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

beforeEach(() => { vi.resetModules();  });
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.useRealTimers(); });
const record = (sessionId = "s1", artifactId = "a1") => ({ sessionId, artifactId,
  displayName: "canonical.md", contentType: "text/markdown", byteSize: 10 });
const response = (value = record()) => ({ ok: true, status: 200, json: async () => value });

describe("artifact HTTP", () => {
  it('reads tenant metadata with current identity and no owner fallback or cross-identity cache', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(response()).mockResolvedValueOnce(response())
      .mockResolvedValueOnce({ ok: false, status: 403 }).mockResolvedValueOnce(response());
    vi.stubGlobal('fetch', fetchMock);
    const { getArtifact } = await import('./artifacts');
    await getArtifact('s1', 'a1', 'tenant-a'); await getArtifact('s1', 'a1', 'tenant-a');
    await expect(getArtifact('s1', 'a1', 'tenant-a')).rejects.toMatchObject({ status: 403 });
    await getArtifact('s1', 'a1', 'tenant-b');
    expect(fetchMock).toHaveBeenCalledTimes(4);
    expect(fetchMock.mock.calls.map(([path]) => path)).toEqual([
      '/api/v2/tenants/tenant-a/sessions/s1/artifacts/a1', '/api/v2/tenants/tenant-a/sessions/s1/artifacts/a1',
      '/api/v2/tenants/tenant-a/sessions/s1/artifacts/a1', '/api/v2/tenants/tenant-b/sessions/s1/artifacts/a1',
    ]);
    for (const [, options] of fetchMock.mock.calls) expect(options).toMatchObject({ credentials: 'same-origin', cache: 'no-store' });
    expect(fetchMock.mock.calls.every(([, options]) => !options.headers)).toBe(true);
  });

  it("failed or foreign metadata is not cached and supports retry", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce({ ok: false, status: 404 })
      .mockResolvedValueOnce(response(record("foreign"))).mockResolvedValue(response());
    vi.stubGlobal("fetch", fetchMock);
    const { getArtifact } = await import("./artifacts");
    await expect(getArtifact("s1", "a1", 'tenant')).rejects.toMatchObject({ status: 404 });
    await expect(getArtifact("s1", "a1", 'tenant')).rejects.toThrow("File unavailable");
    expect(await getArtifact("s1", "a1", 'tenant')).toEqual(record());
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  it("downloads fresh authenticated bytes only on activation, names the file canonically, and disposes URLs", async () => {
    vi.useFakeTimers();
    const blob = new Blob(["exact bytes"]);
    const fetchMock = vi.fn().mockImplementation(async path => String(path).endsWith("/content") ? { ok: true, status: 200, blob: async () => blob } : response());
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
    await getArtifact("s1", "a1", 'tenant');
    expect(create).not.toHaveBeenCalled();
    await downloadArtifact("s1", "a1", undefined, 'tenant');
    await downloadArtifact("s1", "a1", undefined, 'tenant');
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
    let bodies = 0;
    vi.stubGlobal("fetch", vi.fn().mockImplementation(async path => !String(path).endsWith("/content") ? response() : ++bodies === 1 ? { ok: false, status: 500 } : { ok: true, status: 200, blob: async () => new Blob(["bytes"]) }));
    const { downloadArtifact } = await import("./artifacts");
    await expect(downloadArtifact("s1", "a1", undefined, 'tenant')).rejects.toMatchObject({ status: 500 });
    await downloadArtifact("s1", "a1", undefined, 'tenant');
    const controller = new AbortController(); controller.abort();
    await expect(downloadArtifact("s1", "a1", controller.signal, 'tenant')).rejects.toThrow();
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
    const pending = downloadArtifact("s1", "a1", controller.signal, 'tenant');
    const rejected = expect(pending).rejects.toThrow();
    await waitFor(() => expect(body).toHaveBeenCalledOnce());
    controller.abort();
    finishBody(new Blob(["late bytes"]));
    await rejected;
    expect(create).not.toHaveBeenCalled();
    expect(click).not.toHaveBeenCalled();
  });
});
