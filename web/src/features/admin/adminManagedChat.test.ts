import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../../services/adminApi", () => ({
  createAdminAgentInstance: vi.fn()
}));

vi.mock("../../services/api", () => ({
  createSessionForInstance: vi.fn()
}));

vi.mock("../../services/realtime", () => ({
  suspendLiveSessionForNavigation: vi.fn(),
  openSessionById: vi.fn()
}));

vi.mock("../../app/appRoute", () => ({
  navigateToAppPath: vi.fn()
}));

import { navigateToAppPath } from "../../app/appRoute";
import { createSessionForInstance } from "../../services/api";
import { createAdminAgentInstance } from "../../services/adminApi";
import { openSessionById, suspendLiveSessionForNavigation } from "../../services/realtime";
import { startManagedPublicationChat } from "./adminManagedChat";

describe("startManagedPublicationChat", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(createAdminAgentInstance).mockResolvedValue({
      instanceId: "019944af-00d1-7000-8000-000000000001",
      definitionId: "examiner",
      activeVersion: 2,
      compatibility: false,
      lifecycle: "Active",
      revision: 1,
      personaRevision: 1
    });
    vi.mocked(createSessionForInstance).mockResolvedValue({
      sessionId: "019944af-00d1-7000-8000-000000000002",
      agentId: "examiner",
      agentVersion: 2,
      mode: "text",
      pendingMode: null,
      status: "created",
      lastEntrySequence: 0
    });
    vi.mocked(suspendLiveSessionForNavigation).mockResolvedValue(undefined);
    vi.mocked(openSessionById).mockResolvedValue("ready");
    vi.mocked(navigateToAppPath).mockImplementation(() => undefined);
    vi.mocked(openSessionById).mockReset();
    vi.mocked(openSessionById).mockResolvedValue("ready");
  });

  it("opens the session once before navigating to chat", async () => {
    const order: string[] = [];
    vi.mocked(openSessionById).mockImplementation(async () => {
      order.push("open");
      return "ready";
    });
    vi.mocked(navigateToAppPath).mockImplementation(() => {
      order.push("navigate");
    });

    await startManagedPublicationChat("examiner", 2);

    expect(openSessionById).toHaveBeenCalledTimes(1);
    expect(openSessionById).toHaveBeenCalledWith("019944af-00d1-7000-8000-000000000002", { syncUrl: false });
    expect(navigateToAppPath).toHaveBeenCalledWith(
      "/c/019944af-00d1-7000-8000-000000000002",
      true
    );
    expect(order).toEqual(["open", "navigate"]);
  });

  it("throws when the session cannot be opened", async () => {
    vi.mocked(openSessionById).mockResolvedValue("failed");

    await expect(startManagedPublicationChat("examiner", 2)).rejects.toThrow(
      "Managed chat session could not be opened."
    );
    expect(navigateToAppPath).not.toHaveBeenCalled();
  });
});
