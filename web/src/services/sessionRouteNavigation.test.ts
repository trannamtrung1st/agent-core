import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyCatalog, emptySession, useSessionStore } from "../state/sessionStore";

vi.mock("./api", () => ({
  reopenSession: vi.fn(),
  listAgents: vi.fn(),
  getHealth: vi.fn(),
  getSession: vi.fn(),
  listSessionMessages: vi.fn(),
  ensureOwnerCapability: vi.fn().mockResolvedValue("token"),
  createSession: vi.fn(),
  endSession: vi.fn()
}));

vi.mock("./catalog", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./catalog")>();
  return {
    ...actual,
    refreshCatalog: vi.fn().mockResolvedValue(undefined)
  };
});

const signalrMocks = vi.hoisted(() => ({
  start: vi.fn().mockResolvedValue(undefined),
  invoke: vi.fn().mockResolvedValue(undefined)
}));

vi.mock("@microsoft/signalr", () => ({
  HubConnectionBuilder: vi.fn(() => ({
    withUrl: vi.fn().mockReturnThis(),
    withHubProtocol: vi.fn().mockReturnThis(),
    withAutomaticReconnect: vi.fn().mockReturnThis(),
    build: vi.fn(() => ({
      on: vi.fn(),
      start: signalrMocks.start,
      stop: vi.fn().mockResolvedValue(undefined),
      off: vi.fn(),
      invoke: signalrMocks.invoke
    }))
  })),
  HttpTransportType: { WebSockets: 1 },
  HubConnectionState: { Connected: 1 }
}));

vi.mock("@microsoft/signalr-protocol-msgpack", () => ({
  MessagePackHubProtocol: vi.fn()
}));

import { applyRouteFromLocation, openCatalogSession, openSessionById } from "./realtime";
import { getSession, listSessionMessages, reopenSession } from "./api";

describe("session route navigation", () => {
  const endedId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

  beforeEach(() => {
    signalrMocks.start.mockReset();
    signalrMocks.start.mockResolvedValue(undefined);
    signalrMocks.invoke.mockReset();
    signalrMocks.invoke.mockResolvedValue(undefined);
    vi.mocked(reopenSession).mockReset();
    vi.mocked(getSession).mockReset();
    vi.mocked(listSessionMessages).mockReset();
    vi.mocked(getSession).mockResolvedValue({
      sessionId: endedId,
      agentId: "examiner",
      agentVersion: 1,
      mode: "text",
      pendingMode: null,
      status: "ended",
      lastEntrySequence: 1
    });
    vi.mocked(listSessionMessages).mockResolvedValue({
      items: [
        {
          entryId: "e1",
          sequence: 1,
          sourceEventId: "e1",
          role: "user",
          text: "Hello",
          responseId: null,
          status: "completed",
          deliveryMode: "text",
          heardTextEndExclusive: 5,
          receivedTextEndExclusive: 5,
          createdAt: "2026-01-01T00:00:00Z"
        }
      ],
      nextAfter: 1,
      hasMore: false,
      hasOlder: false,
      nextBefore: null
    });
    window.history.replaceState(null, "", `/c/${endedId}`);
    useSessionStore.setState({
      ...emptySession(),
      agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
      selectedAgentId: "examiner",
      ...emptyCatalog(),
      catalogItems: [
        {
          sessionId: endedId,
          title: "Ended chat",
          agentId: "examiner",
          agentVersion: 1,
          status: "ended",
          archived: false,
          ended: true,
          workspaceOwned: false,
          runtimeEpoch: 1,
          revision: 1,
          createdAt: "2026-01-01T00:00:00Z",
          updatedAt: "2026-01-01T00:00:00Z"
        }
      ]
    });
  });

  it("opens ended catalog sessions as read-only history without reopen", async () => {
    const result = await openCatalogSession(
      {
        sessionId: endedId,
        status: "ended",
        archived: false,
        ended: true
      },
      { syncUrl: false }
    );

    expect(result).toBe("ended");
    expect(window.location.pathname).toBe(`/c/${endedId}`);
    expect(useSessionStore.getState().status).toBe("ended");
    expect(useSessionStore.getState().connection).toBe("idle");
    expect(useSessionStore.getState().entries[0]?.text).toBe("Hello");
    expect(useSessionStore.getState().routeNotice).toBeNull();
    expect(reopenSession).not.toHaveBeenCalled();
  });

  it("keeps an ended deep link after applyRouteFromLocation", async () => {
    await applyRouteFromLocation();

    expect(window.location.pathname).toBe(`/c/${endedId}`);
    expect(useSessionStore.getState().status).toBe("ended");
    expect(useSessionStore.getState().entries[0]?.text).toBe("Hello");
    expect(reopenSession).not.toHaveBeenCalled();
  });

  it("opens read-only history when a stale catalog row is already ended", async () => {
    vi.mocked(reopenSession).mockRejectedValue(new Error("Session is ended."));

    const result = await openCatalogSession(
      {
        sessionId: endedId,
        status: "paused",
        archived: false,
        ended: false
      },
      { syncUrl: false }
    );

    expect(result).toBe("ended");
    expect(reopenSession).not.toHaveBeenCalled();
    expect(useSessionStore.getState().status).toBe("ended");
    expect(useSessionStore.getState().connection).toBe("idle");
    expect(useSessionStore.getState().entries[0]?.text).toBe("Hello");
    expect(useSessionStore.getState().error).toBeNull();
  });

  it("opens ended history on the newest page only", async () => {
    const makeEntry = (sequence: number, text: string) => ({
      entryId: `e${sequence}`,
      sequence,
      sourceEventId: `e${sequence}`,
      role: sequence % 2 === 1 ? "user" : "assistant",
      text,
      responseId: sequence % 2 === 0 ? `r${sequence}` : null,
      status: "completed",
      deliveryMode: "text",
      heardTextEndExclusive: text.length,
      receivedTextEndExclusive: text.length,
      createdAt: "2026-01-01T00:00:00Z"
    });
    const newest = Array.from({ length: 50 }, (_, index) => makeEntry(index + 13, `Message ${index + 13}`));

    vi.mocked(getSession).mockResolvedValue({
      sessionId: endedId,
      agentId: "examiner",
      agentVersion: 1,
      mode: "text",
      pendingMode: null,
      status: "ended",
      lastEntrySequence: 62
    });
    vi.mocked(listSessionMessages).mockResolvedValue({
      items: newest,
      nextAfter: 62,
      hasMore: false,
      hasOlder: true,
      nextBefore: 13
    });

    await applyRouteFromLocation();

    expect(listSessionMessages).toHaveBeenCalledTimes(1);
    expect(listSessionMessages).toHaveBeenCalledWith(endedId, expect.objectContaining({ limit: 50 }));
    expect(useSessionStore.getState().entries).toHaveLength(50);
    expect(useSessionStore.getState().entries[0]?.text).toBe("Message 13");
    expect(useSessionStore.getState().entries[49]?.text).toBe("Message 62");
    expect(useSessionStore.getState().historyHasOlder).toBe(true);
  });

  it("does not walk forward pages when opening a long ended transcript", async () => {
    vi.mocked(getSession).mockResolvedValue({
      sessionId: endedId,
      agentId: "examiner",
      agentVersion: 1,
      mode: "text",
      pendingMode: null,
      status: "ended",
      lastEntrySequence: 120
    });
    vi.mocked(listSessionMessages).mockResolvedValue({
      items: [{
        entryId: "e2",
        sequence: 120,
        sourceEventId: "e2",
        role: "assistant",
        text: "Last",
        responseId: "r1",
        status: "completed",
        deliveryMode: "text",
        heardTextEndExclusive: 4,
        receivedTextEndExclusive: 4,
        createdAt: "2026-01-02T00:00:00Z"
      }],
      nextAfter: 120,
      hasMore: false,
      hasOlder: true,
      nextBefore: 71
    });

    await applyRouteFromLocation();

    expect(listSessionMessages).toHaveBeenCalledTimes(1);
    expect(listSessionMessages).toHaveBeenCalledWith(
      endedId,
      expect.objectContaining({ limit: 50 })
    );
    expect(vi.mocked(listSessionMessages).mock.calls[0]?.[1]).not.toHaveProperty("after");
    expect(useSessionStore.getState().entries.map((entry) => entry.text)).toEqual(["Last"]);
    expect(useSessionStore.getState().historyHasOlder).toBe(true);
  });

  it("opens paused catalog sessions without reopening the runtime", async () => {
    const pausedId = "bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee";
    vi.mocked(getSession).mockResolvedValue({
      sessionId: pausedId,
      agentId: "examiner",
      agentVersion: 1,
      mode: "text",
      pendingMode: null,
      status: "paused",
      pauseReason: "inactivity",
      lastEntrySequence: 1
    });
    vi.mocked(listSessionMessages).mockResolvedValue({
      items: [
        {
          entryId: "e1",
          sequence: 1,
          sourceEventId: "e1",
          role: "user",
          text: "Hello",
          responseId: null,
          status: "completed",
          deliveryMode: "text",
          heardTextEndExclusive: 5,
          receivedTextEndExclusive: 5,
          createdAt: "2026-01-01T00:00:00Z"
        }
      ],
      nextAfter: 1,
      hasMore: false,
      hasOlder: false,
      nextBefore: null
    });

    const result = await openCatalogSession(
      {
        sessionId: pausedId,
        status: "paused",
        archived: false,
        ended: false
      },
      { syncUrl: false }
    );

    expect(result).toBe("paused");
    expect(reopenSession).not.toHaveBeenCalled();
    expect(useSessionStore.getState().status).toBe("paused");
    expect(useSessionStore.getState().connection).toBe("idle");
    expect(useSessionStore.getState().pauseReason).toBe("inactivity");
  });

  it("keeps an ended deep link when history load fails", async () => {
    vi.mocked(getSession).mockRejectedValue(new Error("Unable to open the conversation."));

    await applyRouteFromLocation();

    expect(window.location.pathname).toBe(`/c/${endedId}`);
    expect(useSessionStore.getState().status).toBe("ended");
    expect(useSessionStore.getState().connection).toBe("failed");
    expect(useSessionStore.getState().error).toBe("Unable to open the conversation.");
    expect(useSessionStore.getState().routeNotice).toBeNull();
  });

  it("keeps the admin path when openSessionById fails with syncUrl false", async () => {
    const adminPath = "/admin/definitions/examiner";
    const liveId = "ffffffff-ffff-ffff-ffff-ffffffffffff";
    window.history.replaceState(null, "", adminPath);
    vi.mocked(getSession).mockResolvedValue({
      sessionId: liveId,
      agentId: "examiner",
      agentVersion: 1,
      mode: "text",
      pendingMode: null,
      status: "created",
      lastEntrySequence: 0
    });
    vi.mocked(listSessionMessages).mockResolvedValue({
      items: [],
      nextAfter: 0,
      hasMore: false,
      hasOlder: false,
      nextBefore: null
    });
    signalrMocks.start.mockRejectedValueOnce(new Error("connect failed"));

    const result = await openSessionById(liveId, { syncUrl: false });

    expect(result).toBe("failed");
    expect(window.location.pathname).toBe(adminPath);
  });
});
