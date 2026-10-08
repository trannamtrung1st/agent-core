import { afterEach, describe, expect, it, vi } from "vitest";
import { cancelAgentRun, decideAgentRunApproval, listAgentRuns, listBackgroundSessions, continueInChat } from "./api";
import { fixtureRun } from "../features/chat/agentRunFixtures";
describe("Background Session and AgentRun owner API", () => {
  afterEach(() => { window.localStorage.clear(); vi.unstubAllGlobals(); });
  it("uses Session identity, scoped run routes and exact revision approval", async () => {
const storage = new Map<string, string>();
    Object.defineProperty(window, "localStorage", { configurable: true, value: { getItem: (key: string) => storage.get(key) ?? null,
      setItem: (key: string, value: string) => storage.set(key, value), removeItem: (key: string) => storage.delete(key), clear: () => storage.clear() } });
        window.localStorage.setItem("agent-core.owner-capability", "owner-token");
    const fetch = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({ items: [], nextCursor: null, hasMore: false }) });
    vi.stubGlobal("fetch", fetch);
    await listBackgroundSessions("instance-1", "opaque", 20);
    await listAgentRuns("background-1", "run-0", 20);
    await continueInChat("background-1");
    const run = { ...fixtureRun, revision: 4, approval: { approvalId: "approval-1", revision: 2, actionHash: "a".repeat(64), toolName: "email.send", preview: "Send email", expiresAt: fixtureRun.updatedAt } };
    await decideAgentRunApproval(run, "approve");
    expect(fetch.mock.calls[0][0]).toBe("/api/v2/agent-instances/instance-1/background-sessions?limit=20&cursor=opaque");
    expect(fetch.mock.calls[1][0]).toBe("/api/v2/sessions/background-1/agent-runs?limit=20&before=run-0");
    expect(fetch.mock.calls[2][0]).toBe("/api/v2/sessions/background-1/continue-in-chat");
    expect(fetch.mock.calls[3][0]).toBe("/api/v2/sessions/background-1/agent-runs/run-1/approvals/approval-1/approve");
    expect(JSON.parse(fetch.mock.calls[3][1].body)).toEqual({ expectedRevision: 4, expectedApprovalRevision: 2, actionHash: "a".repeat(64) });
    expect((fetch.mock.calls[0][1].headers as Headers).get("X-AgentCore-Owner-Capability")).toBe("owner-token");
    fetch.mockResolvedValueOnce({ ok: false, status: 409, json: async () => ({ detail: "Run revision is stale." }) });
    await expect(cancelAgentRun(run)).rejects.toThrow("Run revision is stale.");
    await expect(decideAgentRunApproval(fixtureRun, "approve")).rejects.toThrow("no longer needs approval");
  });
});
