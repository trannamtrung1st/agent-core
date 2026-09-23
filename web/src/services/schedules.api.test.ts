import { afterEach, describe, expect, it, vi } from "vitest";
import { cancelSessionTrigger, listSessionTriggers } from "./api";

describe("schedule owner fetch", () => {
  afterEach(() => {
    window.localStorage.clear();
    vi.unstubAllGlobals();
  });

  it("loads and cancels a session schedule", async () => {
    window.localStorage.setItem("agent-core.owner-capability", "owner-token");
    const schedule = {
      registrationId: "reg-1",
      intent: "Call John",
      status: "cancelled",
      scheduleKind: "oneShot",
      timeZone: "UTC",
      schedule: "Once on 2026-09-24 at 09:00",
      nextOccurrenceAt: null,
      revision: 2
    };
    const fetchMock = vi.fn()
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({ items: [{ ...schedule, status: "active", revision: 1 }] })
      })
      .mockResolvedValueOnce({
        ok: false,
        status: 409,
        json: async () => ({ title: "Conflict", detail: "Registration revision is stale." })
      });
    vi.stubGlobal("fetch", fetchMock);

    const listed = await listSessionTriggers("session-1");
    expect(listed[0]?.intent).toBe("Call John");
    await expect(cancelSessionTrigger("session-1", "reg-1", 1)).rejects.toThrow("Registration revision is stale.");
    expect(fetchMock.mock.calls[1]?.[0]).toBe("/api/v2/sessions/session-1/triggers/reg-1/cancel");
    const headers = fetchMock.mock.calls[0]?.[1].headers as Headers;
    expect(headers.get("X-AgentCore-Owner-Capability")).toBe("owner-token");
  });
});
