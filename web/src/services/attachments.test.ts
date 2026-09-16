import { afterEach, describe, expect, it, vi } from "vitest";
import { listAttachments } from "./attachments";

describe("attachment HTTP", () => {
  afterEach(() => {
    window.localStorage.clear();
    vi.unstubAllGlobals();
  });

  it("lists with the owner capability header", async () => {
    window.localStorage.setItem("agent-core.owner-capability", "tok");
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => []
    });
    vi.stubGlobal("fetch", fetchMock);
    await listAttachments("s1");
    expect(fetchMock.mock.calls[0][0]).toBe("/api/v2/sessions/s1/attachments");
    const headers = fetchMock.mock.calls[0][1].headers as Headers;
    expect(headers.get("X-AgentCore-Owner-Capability")).toBe("tok");
  });
});
