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

vi.mock("@microsoft/signalr", () => ({
  HubConnectionBuilder: vi.fn(() => ({
    withUrl: vi.fn().mockReturnThis(),
    withHubProtocol: vi.fn().mockReturnThis(),
    withAutomaticReconnect: vi.fn().mockReturnThis(),
    build: vi.fn(() => ({
      on: vi.fn(),
      start: vi.fn().mockResolvedValue(undefined),
      stop: vi.fn().mockResolvedValue(undefined),
      off: vi.fn(),
      invoke: vi.fn()
    }))
  })),
  HttpTransportType: { WebSockets: 1 },
  HubConnectionState: { Connected: 1 }
}));

vi.mock("@microsoft/signalr-protocol-msgpack", () => ({
  MessagePackHubProtocol: vi.fn()
}));

import { applyRouteFromLocation, openCatalogSession } from "./realtime";
import { getSession, listSessionMessages, reopenSession } from "./api";

describe("session route navigation", () => {
  const endedId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

  beforeEach(() => {
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
      hasMore: false
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
    expect(reopenSession).toHaveBeenCalledWith(endedId);
    expect(useSessionStore.getState().status).toBe("ended");
    expect(useSessionStore.getState().connection).toBe("idle");
    expect(useSessionStore.getState().entries[0]?.text).toBe("Hello");
    expect(useSessionStore.getState().error).toBeNull();
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
});
