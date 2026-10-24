import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { emptySession, useSessionStore } from "../state/sessionStore";
import { applySessionModel } from "./realtime";

describe("applySessionModel", () => {
  const fetchMock = vi.fn();
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
    useSessionStore.setState({
      ...emptySession(),
      sessionModelKey: "scripted-alpha",
      sessionModelDisplayName: "Scripted Alpha",
      sessionModelSource: "systemDefault",
      sessionModelEffort: "medium",
      pendingModelKey: "default"
    });
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    useSessionStore.setState({ ...emptySession() });
  });

  it("stages a model and effort for a new conversation without a server mutation", async () => {
    await applySessionModel("scripted-beta", "high");
    expect(useSessionStore.getState()).toMatchObject({
      pendingModelKey: "scripted-beta", pendingReasoningEffort: "high",
      sessionModelKey: "scripted-alpha", sessionModelEffort: "medium",
      modelMutationPending: false
    });
    await applySessionModel("default", null);
    expect(useSessionStore.getState()).toMatchObject({ pendingModelKey: "default", pendingReasoningEffort: null });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it.each(["created", "ready", "paused", "ended"] as const)("keeps an existing %s conversation's saved model read-only", async status => {
    useSessionStore.setState({ sessionId: "saved-session", status });
    const saved = useSessionStore.getState();
    await applySessionModel("scripted-beta", "high");
    expect(useSessionStore.getState()).toBe(saved);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
