import { afterEach, describe, expect, it, vi } from "vitest";
import { emptySession, useSessionStore } from "../state/sessionStore";
import { applySessionModel } from "./realtime";

describe("applySessionModel", () => {
  afterEach(() => {
    window.localStorage.clear();
    vi.unstubAllGlobals();
    useSessionStore.setState({
      ...emptySession(),
      agents: [],
      selectedAgentId: "examiner",
      modelCatalog: [],
      modelCatalogDefaultKey: null
    });
  });

  it("keeps the persisted selection when a live mutation fails", async () => {
    window.localStorage.setItem("agent-core.owner-capability", "token");
    useSessionStore.setState({
      ...emptySession(),
      sessionId: "s1",
      sessionModelKey: "scripted-alpha",
      sessionModelDisplayName: "Scripted Alpha",
      sessionModelSource: "systemDefault",
      sessionModelEffort: "medium",
      pendingModelKey: "default"
    });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue({
      ok: false,
      status: 409,
      json: async () => ({ title: "SessionBusy", detail: "Session is busy generating a response." })
    }));

    await applySessionModel("scripted-beta", null);
    const state = useSessionStore.getState();
    expect(state.sessionModelKey).toBe("scripted-alpha");
    expect(state.sessionModelEffort).toBe("medium");
    expect(state.error).toContain("busy");
  });
});
