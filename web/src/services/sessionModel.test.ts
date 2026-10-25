import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { emptySession, useSessionStore } from "../state/sessionStore";
import { applySessionModel } from "./realtime";
import { tenantSelectChatModel } from "./tenantChat";
import { getSession, type SessionResponse } from "./api";
vi.mock('./tenantChat', async original => ({ ...await original<object>(), tenantSelectChatModel: vi.fn() }));
vi.mock('./api', async original => ({ ...await original<object>(), getSession: vi.fn() }));

describe("applySessionModel", () => {
  const fetchMock = vi.fn();
  beforeEach(() => {
    fetchMock.mockReset();
    vi.mocked(tenantSelectChatModel).mockReset();
    vi.mocked(getSession).mockReset();
    vi.stubGlobal("fetch", fetchMock);
    useSessionStore.setState({
      ...emptySession(),
      sessionOfferingId: "dca30000-0000-4000-8000-000000000001",
      sessionModelDisplayName: "Scripted Alpha",
      sessionModelSource: "tenantDefault",
      sessionModelEffort: "medium",
      pendingOfferingId: "default"
    });
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    useSessionStore.setState({ ...emptySession() });
  });

  it("stages a model and effort for a new conversation without a server mutation", async () => {
    await applySessionModel("dca30000-0000-4000-8000-000000000002", "high");
    expect(useSessionStore.getState()).toMatchObject({
      pendingOfferingId: "dca30000-0000-4000-8000-000000000002", pendingReasoningEffort: "high",
      sessionOfferingId: "dca30000-0000-4000-8000-000000000001", sessionModelEffort: "medium",
      modelMutationPending: false
    });
    await applySessionModel("default", null);
    expect(useSessionStore.getState()).toMatchObject({ pendingOfferingId: "default", pendingReasoningEffort: null });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("persists an idle conversation selection before updating its label and retains the draft", async () => {
    useSessionStore.setState({ sessionId: 'saved-session', connection: 'ready', draft: 'Preserve this draft' });
    let settle!: (value: Awaited<ReturnType<typeof tenantSelectChatModel>>) => void;
    vi.mocked(tenantSelectChatModel).mockReturnValue(new Promise(resolve => { settle = resolve; }));
    const operation = applySessionModel('dca30000-0000-4000-8000-000000000002', null);
    expect(useSessionStore.getState()).toMatchObject({ modelMutationPending: true, sessionModelDisplayName: 'Scripted Alpha' });
    settle({ sessionId: 'saved-session', revision: 2, model: { offeringId: 'dca30000-0000-4000-8000-000000000002',
      displayName: 'Scripted Beta', connectionLabel: 'Offline Synthetic', selectionSource: 'user', reasoningEffort: null } });
    await operation;
    expect(tenantSelectChatModel).toHaveBeenCalledWith('saved-session', 'dca30000-0000-4000-8000-000000000002', null);
    expect(useSessionStore.getState()).toMatchObject({ modelMutationPending: false, sessionModelDisplayName: 'Scripted Beta · Offline Synthetic',
      sessionOfferingId: 'dca30000-0000-4000-8000-000000000002', sessionModelSource: 'user', draft: 'Preserve this draft' });
  });

  it("reads current selection after a refused change without repeating the mutation", async () => {
    useSessionStore.setState({ sessionId: 'saved-session', connection: 'ready', draft: 'Keep exact draft' });
    vi.mocked(tenantSelectChatModel).mockRejectedValue(new Error('Reload current model configuration.'));
    vi.mocked(getSession).mockResolvedValue({ model: { offeringId: 'dca30000-0000-4000-8000-000000000001',
      displayName: 'Scripted Alpha', selectionSource: 'tenantDefault', reasoningEffort: 'medium' } } as SessionResponse);
    await applySessionModel('dca30000-0000-4000-8000-000000000002', null);
    expect(tenantSelectChatModel).toHaveBeenCalledTimes(1); expect(getSession).toHaveBeenCalledTimes(1);
    expect(useSessionStore.getState()).toMatchObject({ sessionOfferingId: 'dca30000-0000-4000-8000-000000000001',
      sessionModelDisplayName: 'Scripted Alpha', modelMutationPending: false, draft: 'Keep exact draft', error: 'Reload current model configuration.' });
  });

  it("does not mutate a running conversation or replace another conversation after a late response", async () => {
    useSessionStore.setState({ sessionId: 'saved-session', connection: 'ready', liveResponseId: 'running-response' });
    await applySessionModel('default', null);
    expect(tenantSelectChatModel).not.toHaveBeenCalled();
    useSessionStore.setState({ liveResponseId: null });
    let settle!: (value: Awaited<ReturnType<typeof tenantSelectChatModel>>) => void;
    vi.mocked(tenantSelectChatModel).mockReturnValue(new Promise(resolve => { settle = resolve; }));
    const operation = applySessionModel('default', null);
    useSessionStore.setState({ ...emptySession(), sessionId: 'another-session', sessionModelDisplayName: 'Another model' });
    settle({ sessionId: 'saved-session', revision: 2, model: null }); await operation;
    expect(useSessionStore.getState()).toMatchObject({ sessionId: 'another-session', sessionModelDisplayName: 'Another model', modelMutationPending: false });
  });

  it.each(["created", "ready", "paused", "ended"] as const)("keeps a disconnected %s conversation's saved model unchanged", async status => {
    useSessionStore.setState({ sessionId: "saved-session", status });
    const saved = useSessionStore.getState();
    await applySessionModel("dca30000-0000-4000-8000-000000000002", "high");
    expect(useSessionStore.getState()).toBe(saved);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
