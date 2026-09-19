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
    expect(state.modelMutationPending).toBe(false);
  });

  it("does not apply a stale session response after switching chats", async () => {
    window.localStorage.setItem("agent-core.owner-capability", "token");
    useSessionStore.setState({
      ...emptySession(),
      sessionId: "session-a",
      sessionModelKey: "scripted-alpha",
      sessionModelDisplayName: "Scripted Alpha",
      sessionModelSource: "user",
      sessionModelEffort: "medium"
    });

    let resolveFetch: (value: unknown) => void = () => undefined;
    const fetchPromise = new Promise((resolve) => {
      resolveFetch = resolve;
    });
    vi.stubGlobal(
      "fetch",
      vi.fn(() =>
        fetchPromise.then(() => ({
          ok: true,
          status: 200,
          json: async () => ({
            sessionId: "session-a",
            model: {
              catalogKey: "scripted-beta",
              displayName: "Scripted Beta",
              selectionSource: "user",
              reasoningEffort: "high",
              modelId: "scripted-beta"
            }
          })
        }))
      )
    );

    const pending = applySessionModel("scripted-beta", "high");
    useSessionStore.setState({
      sessionId: "session-b",
      sessionModelKey: "scripted-alpha",
      sessionModelDisplayName: "Scripted Alpha",
      sessionModelSource: "user",
      sessionModelEffort: "low"
    });

    resolveFetch(undefined);
    await pending;

    const state = useSessionStore.getState();
    expect(state.sessionId).toBe("session-b");
    expect(state.sessionModelKey).toBe("scripted-alpha");
    expect(state.sessionModelEffort).toBe("low");
    expect(state.modelMutationPending).toBe(false);
  });

  it("does not clear a newer session mutation when an older one completes", async () => {
    window.localStorage.setItem("agent-core.owner-capability", "token");
    useSessionStore.setState({
      ...emptySession(),
      sessionId: "session-a",
      sessionModelKey: "scripted-alpha",
      sessionModelDisplayName: "Scripted Alpha",
      sessionModelSource: "user",
      sessionModelEffort: "medium"
    });

    let resolveA: (value: unknown) => void = () => undefined;
    const fetchA = new Promise((resolve) => {
      resolveA = resolve;
    });
    let resolveB: (value: unknown) => void = () => undefined;
    const fetchB = new Promise((resolve) => {
      resolveB = resolve;
    });
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(() =>
        fetchA.then(() => ({
          ok: true,
          status: 200,
          json: async () => ({
            sessionId: "session-a",
            model: {
              catalogKey: "scripted-beta",
              displayName: "Scripted Beta",
              selectionSource: "user",
              reasoningEffort: "high",
              modelId: "scripted-beta"
            }
          })
        }))
      )
      .mockImplementationOnce(() =>
        fetchB.then(() => ({
          ok: true,
          status: 200,
          json: async () => ({
            sessionId: "session-b",
            model: {
              catalogKey: "scripted-gamma",
              displayName: "Scripted Gamma",
              selectionSource: "user",
              reasoningEffort: "low",
              modelId: "scripted-gamma"
            }
          })
        }))
      );
    vi.stubGlobal("fetch", fetchMock);

    const pendingA = applySessionModel("scripted-beta", "high");
    useSessionStore.setState({
      sessionId: "session-b",
      sessionModelKey: "scripted-alpha",
      sessionModelEffort: "low",
      modelMutationPending: false,
      modelMutationOwner: null
    });
    const pendingB = applySessionModel("scripted-gamma", "low");
    expect(useSessionStore.getState().modelMutationPending).toBe(true);

    resolveA(undefined);
    await pendingA;
    expect(useSessionStore.getState().modelMutationPending).toBe(true);
    expect(useSessionStore.getState().sessionModelKey).toBe("scripted-alpha");

    resolveB(undefined);
    await pendingB;
    const state = useSessionStore.getState();
    expect(state.sessionModelKey).toBe("scripted-gamma");
    expect(state.sessionModelEffort).toBe("low");
    expect(state.modelMutationPending).toBe(false);
  });
});
