import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App } from "antd";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BackgroundWorkDrawer } from "./BackgroundWorkDrawer";
import { AgentRunDetails } from "./AgentRunDetails";
import { WORK_READ_STORAGE_KEY } from "./workReadState";
import { fixtureBackground, fixtureRun } from "./agentRunFixtures";
import { getInstanceAgentRun, cancelAgentRun, decideAgentRunApproval, continueInChat, getBackgroundSession, listAgentRuns, listBackgroundSessions } from "../../services/api";
import { openCatalogSession } from "../../services/realtime";
import { refreshCatalog } from "../../services/catalog";
vi.mock("../../services/api", async original => ({ ...await original<object>(),
  getInstanceAgentRun: vi.fn(), listBackgroundSessions: vi.fn(), listAgentRuns: vi.fn(), getBackgroundSession: vi.fn(), continueInChat: vi.fn(), cancelAgentRun: vi.fn(), decideAgentRunApproval: vi.fn()
}));
vi.mock("../../services/realtime", () => ({ openCatalogSession: vi.fn() }));
vi.mock("../../services/catalog", () => ({ refreshCatalog: vi.fn() }));
vi.mock("../../services/artifacts", async original => ({ ...await original<object>(), listArtifactPage: vi.fn(async () => ({ items: [], nextCursor: null, hasMore: false })) }));
beforeEach(() => {
  const storage = new Map<string, string>();
    Object.defineProperty(window, "localStorage", { configurable: true, value: { getItem: (key: string) => storage.get(key) ?? null,
      setItem: (key: string, value: string) => storage.set(key, value), removeItem: (key: string) => storage.delete(key), clear: () => storage.clear() } });
    vi.clearAllMocks(); window.localStorage.clear();
  vi.mocked(listBackgroundSessions).mockResolvedValue({ items: [fixtureBackground], nextCursor: null, hasMore: false });
  vi.mocked(listAgentRuns).mockResolvedValue({ items: [fixtureRun], nextCursor: null, hasMore: false });
  vi.mocked(getBackgroundSession).mockResolvedValue({ ...fixtureBackground, surfaces: ["ChatList", "BackgroundWork"] });
  vi.mocked(continueInChat).mockResolvedValue({ sessionId: fixtureBackground.session.sessionId });
  vi.mocked(openCatalogSession).mockResolvedValue("ready");
});
const show = (instanceId = "instance-1") => render(<App><BackgroundWorkDrawer instanceId={instanceId} open wide onClose={() => undefined} /></App>);
describe("Background Sessions", () => {
  it("shows one exact-detail error without a lingering spinner and retries that Session", async () => {
    const owned = { ...fixtureBackground, session: { ...fixtureBackground.session, agentInstanceId: "instance-1" } };
    vi.mocked(getBackgroundSession).mockRejectedValueOnce(new Error("Original result is unavailable"))
      .mockResolvedValueOnce(owned);
    render(<App><BackgroundWorkDrawer instanceId="instance-1" open wide initialSessionId={fixtureBackground.session.sessionId} onClose={vi.fn()} /></App>);
    expect(await screen.findByText("Original result is unavailable")).toBeVisible();
    expect(screen.getAllByRole("alert")).toHaveLength(1);
    expect(screen.queryByLabelText("Loading background Session")).not.toBeInTheDocument();
    expect(listBackgroundSessions).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Retry background session" }));
    expect(await screen.findByRole("button", { name: "Continue in chat" })).toBeVisible();
    expect(getBackgroundSession).toHaveBeenLastCalledWith(fixtureBackground.session.sessionId);
    expect(screen.queryByText("Original result is unavailable")).not.toBeInTheDocument();
  });
  it("ignores a late exact-detail response after navigating to another background Session", async () => {
    let finish!: (value: typeof fixtureBackground) => void;
    vi.mocked(getBackgroundSession).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
    const next = { ...fixtureBackground, session: { ...fixtureBackground.session, agentInstanceId: "instance-1", sessionId: "next-session" }, originalTitle: "Next original task" };
    vi.mocked(getBackgroundSession).mockResolvedValueOnce(next);
    const view = render(<App><BackgroundWorkDrawer instanceId="instance-1" open wide initialSessionId={fixtureBackground.session.sessionId} onClose={vi.fn()} /></App>);
    view.rerender(<App><BackgroundWorkDrawer instanceId="instance-1" open wide initialSessionId="next-session" onClose={vi.fn()} /></App>);
    expect(await screen.findByRole("dialog", { name: "Next original task" })).toBeVisible();
    await act(async () => { finish(fixtureBackground); });
    expect(screen.getByRole("dialog", { name: "Next original task" })).toBeVisible();
  });
  it("marks attention results across unloaded pages without acknowledging quiet or handled results", async () => {
    const attention = { ...fixtureRun, outcome: { ...fixtureRun.outcome!, attentionRequired: true } };
    const next = { ...fixtureBackground, session: { ...fixtureBackground.session, sessionId: "second" }, initialRun: { ...attention, agentRunId: "second-run" } };
    vi.mocked(listBackgroundSessions).mockImplementation(async (_owner, cursor) => {
      if (cursor) return { items: [next, { ...fixtureBackground, initialRun: { ...attention, agentRunId: "handled" }, completionDelivery: { status: "handled", targetSessionId: "parent", parentAgentRunId: "handling-run", reason: null } }, fixtureBackground], hasMore: false, nextCursor: null };
      return { items: [{ ...fixtureBackground, initialRun: attention }], hasMore: true, nextCursor: "opaque-next" };
    });
    show();
    expect(await screen.findByText("Unread · needs attention")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Mark all as read" }));
    await waitFor(() => expect(screen.queryByText("Unread · needs attention")).not.toBeInTheDocument());
    expect(listBackgroundSessions).toHaveBeenCalledWith("instance-1", "opaque-next", 100);
    expect(Object.keys(JSON.parse(localStorage.getItem(WORK_READ_STORAGE_KEY)!)).sort()).toEqual(["run-1", "second-run"]);
    expect(continueInChat).not.toHaveBeenCalled();
    expect(screen.getByRole("status")).toHaveTextContent("Read status saved in this browser.");
  });
  it("keeps all results unread after a failed later page and supports retry", async () => {
    const attention = { ...fixtureBackground, initialRun: { ...fixtureRun, outcome: { ...fixtureRun.outcome!, attentionRequired: true } } };
    let fail = true;
    vi.mocked(listBackgroundSessions).mockImplementation(async (_owner, cursor) => {
      if (cursor && fail) throw new Error("Unable to load next page");
      return { items: cursor ? [] : [attention], hasMore: !cursor, nextCursor: cursor ? null : "next" };
    });
    show(); await screen.findByText("Unread · needs attention");
    fireEvent.click(screen.getByRole("button", { name: "Mark all as read" }));
    expect(await screen.findByText("Unable to load next page")).toBeVisible();
    expect(localStorage.getItem(WORK_READ_STORAGE_KEY)).toBeNull();
    expect(screen.getByText("Unread · needs attention")).toBeVisible();
    fail = false; fireEvent.click(screen.getByRole("button", { name: "Retry mark all as read" }));
    await waitFor(() => expect(screen.queryByText("Unread · needs attention")).not.toBeInTheDocument());
    expect(screen.queryByText("Unable to load next page")).not.toBeInTheDocument();
  });
  it("shows a storage failure and allows bulk read to recover", async () => {
    vi.mocked(listBackgroundSessions).mockResolvedValue({ items: [{ ...fixtureBackground, initialRun: { ...fixtureRun, outcome: { ...fixtureRun.outcome!, attentionRequired: true } } }], hasMore: false, nextCursor: null });
    const saved = window.localStorage.setItem;
    window.localStorage.setItem = () => { throw new Error("storage blocked"); };
    show(); await screen.findByText("Unread · needs attention");
    fireEvent.click(screen.getByRole("button", { name: "Mark all as read" }));
    expect(await screen.findByText("Unable to save read status in this browser. Try again.")).toBeVisible();
    expect(screen.getByText("Unread · needs attention")).toBeVisible();
    window.localStorage.setItem = saved;
    fireEvent.click(screen.getByRole("button", { name: "Retry mark all as read" }));
    await waitFor(() => expect(screen.queryByText("Unread · needs attention")).not.toBeInTheDocument());
  });
  it("does not acknowledge results when pagination repeats a cursor", async () => {
    vi.mocked(listBackgroundSessions).mockResolvedValue({ items: [{ ...fixtureBackground, initialRun: { ...fixtureRun, outcome: { ...fixtureRun.outcome!, attentionRequired: true } } }], hasMore: true, nextCursor: "repeat" });
    show(); await screen.findByText("Unread · needs attention");
    fireEvent.click(screen.getByRole("button", { name: "Mark all as read" }));
    expect(await screen.findByText("Unable to load all background results. Try marking all as read again.")).toBeVisible();
    expect(localStorage.getItem(WORK_READ_STORAGE_KEY)).toBeNull();
  });
  it("ignores a pending bulk-read response after the owner changes", async () => {
    const attention = { ...fixtureBackground, initialRun: { ...fixtureRun, outcome: { ...fixtureRun.outcome!, attentionRequired: true } } };
    let resolve!: (value: Awaited<ReturnType<typeof listBackgroundSessions>>) => void;
    vi.mocked(listBackgroundSessions).mockImplementation(async (_owner, _cursor, limit) => limit === 100
      ? new Promise(done => { resolve = done; }) : { items: [attention], hasMore: false, nextCursor: null });
    const view = show(); await screen.findByText("Unread · needs attention");
    fireEvent.click(screen.getByRole("button", { name: "Mark all as read" }));
    await waitFor(() => expect(resolve).toBeDefined());
    view.rerender(<App><BackgroundWorkDrawer instanceId="instance-2" open wide onClose={() => undefined} /></App>);
    await act(async () => resolve({ items: [attention], hasMore: false, nextCursor: null }));
    expect(localStorage.getItem(WORK_READ_STORAGE_KEY)).toBeNull();
  });
  it("previews the original result and bounded file count with its exact Automation source", async () => {
    vi.mocked(listBackgroundSessions).mockResolvedValue({ items: [{ ...fixtureBackground, artifactCount: 50, artifactCountHasMore: true,
      origin: { ...fixtureBackground.origin, automationId: "90000000-0000-4000-8000-000000000010" } }], nextCursor: null, hasMore: false });
    show("90000000-0000-4000-8000-000000000001");
    expect(await screen.findByText("50+ files")).toBeInTheDocument();
    expect(screen.getByText("The background check finished.")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Progress check", level: 5 })).toBeVisible();
    expect(screen.queryByRole("button", { name: "Progress check" })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "View Automation" })).toHaveAttribute("href",
      "/admin/instances/90000000-0000-4000-8000-000000000001/automation/automations?automation=90000000-0000-4000-8000-000000000010");
  });
  it("dismisses with Escape inside the drawer without reaching its parent", async () => {
    const close = vi.fn(); const parentKeyDown = vi.fn();
    render(<div onKeyDown={parentKeyDown}><App><BackgroundWorkDrawer instanceId="instance-1" open wide onClose={close} /></App></div>);
    const history = await screen.findByRole("button", { name: "View original result" });
    history.focus(); fireEvent.keyDown(history, { key: "Escape" });
    expect(close).toHaveBeenCalledOnce();
    expect(parentKeyDown).not.toHaveBeenCalled();
  });
  it("opens a Session's run history and preserves bounded reading regions", async () => {
    show(); fireEvent.click(await screen.findByRole("button", { name: "View original result" }));
    expect(await screen.findByText("The background check finished.")).toBeInTheDocument();
    fireEvent.click(screen.getByText("Conversation run history"));
    await waitFor(() => expect(listAgentRuns).toHaveBeenCalledWith("background-1", undefined));
    expect(screen.getAllByRole("region", { name: "Response" })[0]).toHaveAttribute("tabindex", "0");
    fireEvent.click(screen.getByRole("button", { name: "All background Sessions" }));
    expect(screen.getByRole("heading", { name: "Progress check" })).toBeInTheDocument();
  });
  it("moves focus into history and returns to the control that opened it", async () => {
    show();
    const opener = await screen.findByRole("button", { name: "View original result" });
    opener.focus(); fireEvent.click(opener);
    const back = await screen.findByRole("button", { name: "All background Sessions" });
    expect(back).toHaveFocus();
    fireEvent.click(back);
    expect(screen.getByRole("button", { name: "View original result" })).toHaveFocus();
  });
  it("continues the same Session in chat and refreshes the chat rail", async () => {
    show(); fireEvent.click(await screen.findByRole("button", { name: "Continue in chat" }));
    await waitFor(() => expect(openCatalogSession).toHaveBeenCalledWith(fixtureBackground.session));
    expect(continueInChat).toHaveBeenCalledWith("background-1"); expect(refreshCatalog).toHaveBeenCalledWith(true);
  });
  it.each([
    ["failed", "The chat connection could not be opened. Try opening chat again."],
    ["blocked", "This Session is archived. Refresh to update its availability."]
  ] as const)("keeps a %s chat opening visible and allows recovery", async (result, message) => {
    vi.mocked(openCatalogSession).mockResolvedValueOnce(result).mockResolvedValueOnce("ready");
    const close = vi.fn();
    render(<App><BackgroundWorkDrawer instanceId="instance-1" open wide onClose={close} /></App>);
    fireEvent.click(await screen.findByRole("button", { name: "Continue in chat" }));
    expect(await screen.findByText(message)).toBeInTheDocument();
    expect(close).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Open chat" }));
    await waitFor(() => expect(close).toHaveBeenCalledOnce());
    expect(continueInChat).toHaveBeenCalledOnce();
    expect(screen.queryByText(message)).not.toBeInTheDocument();
  });
  it("reveals the read-only chat if the Session ends while opening it", async () => {
    vi.mocked(openCatalogSession).mockResolvedValue("ended");
    const close = vi.fn();
    render(<App><BackgroundWorkDrawer instanceId="instance-1" open wide onClose={close} /></App>);
    fireEvent.click(await screen.findByRole("button", { name: "Continue in chat" }));
    await waitFor(() => expect(close).toHaveBeenCalledOnce());
  });
  it("opens an existing chat without foregrounding or replacing the original result", async () => {
    vi.mocked(listBackgroundSessions).mockResolvedValue({ items: [{ ...fixtureBackground, surfaces: ["ChatList", "BackgroundWork"],
      session: { ...fixtureBackground.session, title: "Follow-up conversation" } }], nextCursor: null, hasMore: false });
    show();
    expect(await screen.findByRole("heading", { name: "Progress check" })).toBeVisible();
    expect(screen.getByText("Continued in chat")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Open chat" }));
    await waitFor(() => expect(openCatalogSession).toHaveBeenCalledWith(fixtureBackground.session));
    expect(continueInChat).not.toHaveBeenCalled();
  });
  it("does not foreground again after metadata refresh fails following successful continuation", async () => {
    vi.mocked(getBackgroundSession).mockRejectedValueOnce(new Error("Session could not be refreshed."));
    const close = vi.fn();
    render(<App><BackgroundWorkDrawer instanceId="instance-1" open wide onClose={close} /></App>);
    fireEvent.click(await screen.findByRole("button", { name: "Continue in chat" }));
    await screen.findByText("Session could not be refreshed.");
    fireEvent.click(screen.getByRole("button", { name: "Open chat" }));
    await waitFor(() => expect(close).toHaveBeenCalledOnce());
    expect(continueInChat).toHaveBeenCalledOnce();
  });
  it("keeps an unavailable historical initial result separate from mutable chat presentation", async () => {
    vi.mocked(listBackgroundSessions).mockResolvedValue({ items: [{ ...fixtureBackground, originalTitle: null, initialRun: null,
      session: { ...fixtureBackground.session, title: "New conversation title" } }], nextCursor: null, hasMore: false });
    show();
    expect(await screen.findByRole("heading", { name: "Background task" })).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "View original result" }));
    expect(await screen.findByText("The original run is unavailable.")).toBeVisible();
    expect(screen.queryByText("New conversation title")).not.toBeInTheDocument();
  });
  it("never opens an old Session after changing owner during admission", async () => {
    let finish!: (value: { sessionId: string }) => void;
    vi.mocked(continueInChat).mockReturnValue(new Promise(resolve => { finish = resolve; }));
    const view = show(); fireEvent.click(await screen.findByRole("button", { name: "Continue in chat" }));
    view.rerender(<App><BackgroundWorkDrawer instanceId="instance-2" open wide onClose={() => undefined} /></App>);
    await act(async () => finish({ sessionId: "background-1" }));
    expect(openCatalogSession).not.toHaveBeenCalled();
  });
  it("keeps failure and recovery inline", async () => {
    vi.mocked(listBackgroundSessions).mockRejectedValueOnce(new Error("Background Sessions could not be loaded."));
    show(); expect(await screen.findByText("Background Sessions could not be loaded.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByRole("heading", { name: "Progress check" })).toBeInTheDocument();
  });
  it("uses the server cursor for bounded pagination", async () => {
    vi.mocked(listBackgroundSessions).mockResolvedValueOnce({ items: [fixtureBackground], nextCursor: "opaque-cursor", hasMore: true });
    vi.mocked(listBackgroundSessions).mockResolvedValueOnce({ items: [{ ...fixtureBackground, session: { ...fixtureBackground.session, sessionId: "background-2", title: "Another task" } }], nextCursor: null, hasMore: false });
    show(); fireEvent.click(await screen.findByRole("button", { name: "Load more" }));
    await waitFor(() => expect(listBackgroundSessions).toHaveBeenCalledWith("instance-1", "opaque-cursor"));
  });
  it("preserves archive boundaries and quiet NoAction", async () => {
    vi.mocked(listBackgroundSessions).mockResolvedValue({ items: [{ ...fixtureBackground, canContinueInChat: false }], nextCursor: null, hasMore: false });
    vi.mocked(listBackgroundSessions).mockResolvedValue({ items: [{ ...fixtureBackground, canContinueInChat: false, initialRun: { ...fixtureRun, outcome: { kind: "NoAction", summary: "", outcomeEntryId: null, attentionRequired: false } } }], nextCursor: null, hasMore: false });
    show(); expect(await screen.findByRole("button", { name: "Continue in chat" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "View original result" }));
    expect(await screen.findByText("No action was needed.")).toBeInTheDocument();
  });
  it("sends cancellation only after review and retains a stale-action error", async () => {
    const run = { ...fixtureRun, status: "running", cancellationAvailable: true };
    vi.mocked(cancelAgentRun).mockRejectedValue(new Error("Run revision is stale. Refresh and try again."));
    render(<App><AgentRunDetails run={run} onChange={() => undefined} /></App>);
    fireEvent.click(screen.getByRole("button", { name: "Cancel run" }));
    expect(cancelAgentRun).not.toHaveBeenCalled();
    fireEvent.click(screen.getAllByRole("button", { name: "Cancel run" })[1]);
    await waitFor(() => expect(cancelAgentRun).toHaveBeenCalledWith(run));
    expect(await screen.findByText("Run revision is stale. Refresh and try again.")).toBeInTheDocument();
  });
  it("does not submit a confirmation after switching to a different run", async () => {
    const run = { ...fixtureRun, status: "running", cancellationAvailable: true };
    const view = render(<App><AgentRunDetails run={run} onChange={() => undefined} /></App>);
    fireEvent.click(screen.getByRole("button", { name: "Cancel run" }));
    view.rerender(<App><AgentRunDetails run={{ ...run, agentRunId: "another-run" }} onChange={() => undefined} /></App>);
    fireEvent.click(screen.getAllByRole("button", { name: "Cancel run" })[1]);
    await waitFor(() => expect(screen.queryByText("Cancel this run?")).not.toBeInTheDocument());
    expect(cancelAgentRun).not.toHaveBeenCalled();
  });
  it("requires a fresh review if the run changes while approval confirmation is open", async () => {
    const run = { ...fixtureRun, status: "needsApproval", approval: { approvalId: "approval-1", revision: 1,
      actionHash: "exact-hash", toolName: "browser.act", preview: "Submit this form", expiresAt: new Date(Date.now() + 60_000).toISOString() } };
    const view = render(<App><AgentRunDetails run={run} onChange={() => undefined} /></App>);
    fireEvent.click(screen.getByRole("button", { name: "Approve action" }));
    view.rerender(<App><AgentRunDetails run={{ ...run, revision: run.revision + 1 }} onChange={() => undefined} /></App>);
    fireEvent.click(screen.getAllByRole("button", { name: "Approve action" })[1]);
    expect(await screen.findByText("The run changed or the approval expired. Review the current details and try again.")).toBeInTheDocument();
    expect(decideAgentRunApproval).not.toHaveBeenCalled();
  });
  it("makes expired approvals readable and disables both decisions", () => {
    render(<App><AgentRunDetails run={{ ...fixtureRun, status: "needsApproval", approval: { approvalId: "approval-1", revision: 1,
      actionHash: "exact-hash", toolName: "browser.act", preview: "Submit this form", expiresAt: new Date(Date.now() - 1000).toISOString() } }} onChange={() => undefined} /></App>);
    expect(screen.getByRole("button", { name: "Approve action" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Reject action" })).toBeDisabled();
    expect(screen.getByText("Approval expired. Waiting for the run to update.")).toBeInTheDocument();
  });
});


describe("Completion accounting and wait presentation", () => {
  it("shows a typed wait separately from retry and keeps cancellation available", () => {
    render(<App><AgentRunDetails run={{ ...fixtureRun, status: "waitingForSignal", cancellationAvailable: true,
      wait: { mode: "Background", until: "All", backgroundSessionIds: ["child-session"], deadline: "2026-10-08T14:00:00Z" } }} onChange={vi.fn()} /></App>);
    expect(screen.getByLabelText("Execution wait")).toHaveTextContent("Waiting for all background results");
    expect(screen.getByRole("button", { name: "Cancel run" })).toBeEnabled();
    expect(screen.queryByText(/Retry scheduled/)).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Conversation child-se/ })).toHaveAttribute("href", "/c/child-session");
  });
  it("opens the exact durable handling Run without rendering a completion bubble", async () => {
    vi.mocked(listBackgroundSessions).mockResolvedValue({ items: [{ ...fixtureBackground,
      completionDelivery: { status: "handled", targetSessionId: "parent-session", parentAgentRunId: "handling-run", reason: null } }], hasMore: false, nextCursor: null });
    vi.mocked(getInstanceAgentRun).mockResolvedValue({ ...fixtureRun, agentRunId: "handling-run" });
    render(<App><BackgroundWorkDrawer instanceId="instance" open wide onClose={vi.fn()} /></App>);
    await screen.findByText(/Handled in conversation/);
    expect(screen.queryByText(/Unread · needs attention/)).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "View handling run" }));
    await waitFor(() => expect(getInstanceAgentRun).toHaveBeenCalledWith("instance", "handling-run"));
    // rc-util deliberately uses the same generated ID in NODE_ENV=test; scope by the actual drawer heading.
    await waitFor(() => expect(screen.getAllByRole("dialog").some(dialog => within(dialog).queryByText("Run details", { exact: true }) !== null)).toBe(true));
    expect(screen.queryByText("Background work completed")).not.toBeInTheDocument();
  });
});
