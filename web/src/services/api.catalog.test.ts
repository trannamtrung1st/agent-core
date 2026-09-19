import { afterEach, describe, expect, it, vi } from "vitest";
import { createSession, deleteAllSessions, durableDeleteSession, listCatalog } from "./api";

describe("catalog owner fetch", () => {
  afterEach(() => {
    window.localStorage.clear();
    vi.unstubAllGlobals();
  });

  it("sends the restored capability header", async () => {
    window.localStorage.setItem("agent-core.owner-capability", "restored-token");
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({ items: [], nextCursor: null, hasMore: false })
    });
    vi.stubGlobal("fetch", fetchMock);
    await listCatalog();
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/v2/sessions",
      expect.objectContaining({
        headers: expect.any(Headers)
      })
    );
    const headers = fetchMock.mock.calls[0][1].headers as Headers;
    expect(headers.get("X-AgentCore-Owner-Capability")).toBe("restored-token");
  });

  it("reissues after 401 then fail-closes", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce({ ok: true, json: async () => ({ token: "t1" }) })
      .mockResolvedValueOnce({ ok: false, status: 401 })
      .mockResolvedValueOnce({ ok: true, json: async () => ({ token: "t2" }) })
      .mockResolvedValueOnce({ ok: false, status: 401 });
    vi.stubGlobal("fetch", fetchMock);
    await expect(createSession("examiner", 1)).rejects.toThrow("Local owner access is unavailable.");
    expect(window.localStorage.getItem("agent-core.owner-capability")).toBeNull();
  });

  it("uses bulk durable delete on the catalog collection", async () => {
    window.localStorage.setItem("agent-core.owner-capability", "tok");
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({ deletedCount: 3 })
    });
    vi.stubGlobal("fetch", fetchMock);
    await expect(deleteAllSessions({ includeArchived: true })).resolves.toBe(3);
    expect(fetchMock.mock.calls[0][0]).toBe("/api/v2/sessions?includeArchived=true");
    expect(fetchMock.mock.calls[0][1].method).toBe("DELETE");
  });

  it("uses server-owned durable delete", async () => {
    window.localStorage.setItem("agent-core.owner-capability", "tok");
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 204 });
    vi.stubGlobal("fetch", fetchMock);
    await durableDeleteSession("s1");
    expect(fetchMock.mock.calls[0][0]).toBe("/api/v2/sessions/s1");
    expect(fetchMock.mock.calls[0][1].method).toBe("DELETE");
  });
});
