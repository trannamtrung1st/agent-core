import { afterEach, describe, expect, it, vi } from "vitest";
import { approveWorkItem, cancelWorkItem, getWorkItemResult, listWorkItems, rejectWorkItem } from "./api";

const queued = {
  workItemId: "work-1",
  status: "queued",
  revision: 2,
  origin: "Scheduled reminder",
  progress: null,
  needsApproval: false,
  approvalId: null,
  approvalRevision: null,
  approvalPreview: null,
  actionHash: null,
  cancellationAvailable: true,
  failureCode: null,
  failureSummary: null,
  knownEffect: null,
  createdAt: "2026-09-24T09:00:00.000Z",
  updatedAt: "2026-09-24T09:00:00.000Z"
};

describe("background work owner fetch", () => {
  afterEach(() => {
    window.localStorage.clear();
    vi.unstubAllGlobals();
  });

  it("loads, reads a result, and sends revision-bound cancel and approval", async () => {
    window.localStorage.setItem("agent-core.owner-capability", "owner-token");
    const fetchMock = vi.fn()
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({ items: [queued] })
      })
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({ workItemId: "work-1", text: "Oven timer finished.", completedAt: queued.updatedAt })
      })
      .mockResolvedValueOnce({
        ok: false,
        status: 409,
        json: async () => ({ title: "Conflict", detail: "Work revision is stale." })
      })
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({ ...queued, status: "needsApproval" })
      })
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({ ...queued, status: "queued", needsApproval: false })
      });
    vi.stubGlobal("fetch", fetchMock);

    const listed = await listWorkItems("session-1");
    expect(listed[0]?.origin).toBe("Scheduled reminder");
    const result = await getWorkItemResult("session-1", "work-1");
    expect(result.text).toBe("Oven timer finished.");
    await expect(cancelWorkItem("session-1", "work-1", 1)).rejects.toThrow("Work revision is stale.");
    await approveWorkItem("session-1", "work-1", "approval-1", 4, 1, "a".repeat(64));
    await rejectWorkItem("session-1", "work-1", "approval-1", 4, 1, "a".repeat(64));

    expect(fetchMock.mock.calls[0]?.[0]).toBe("/api/v2/sessions/session-1/work-items");
    expect(fetchMock.mock.calls[1]?.[0]).toBe("/api/v2/sessions/session-1/work-items/work-1/result");
    expect(fetchMock.mock.calls[2]?.[0]).toBe("/api/v2/sessions/session-1/work-items/work-1/cancel");
    expect(fetchMock.mock.calls[3]?.[0]).toBe("/api/v2/sessions/session-1/work-items/work-1/approvals/approval-1/approve");
    expect(fetchMock.mock.calls[4]?.[0]).toBe("/api/v2/sessions/session-1/work-items/work-1/approvals/approval-1/reject");
    const headers = fetchMock.mock.calls[0]?.[1].headers as Headers;
    expect(headers.get("X-AgentCore-Owner-Capability")).toBe("owner-token");
    expect(JSON.parse(String(fetchMock.mock.calls[3]?.[1].body))).toEqual({
      expectedRevision: 4,
      expectedApprovalRevision: 1,
      actionHash: "a".repeat(64)
    });
  });
});
