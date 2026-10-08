import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { App } from "antd";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BackgroundWorkDrawer } from "./BackgroundWorkDrawer";
import { AgentRunDetails } from "./AgentRunDetails";
import { fixtureBackground, fixtureRun } from "./agentRunFixtures";
import { cancelAgentRun, decideAgentRunApproval, continueInChat, getBackgroundSession, listAgentRuns, listBackgroundSessions } from "../../services/api";
import { openCatalogSession } from "../../services/realtime";
import { refreshCatalog } from "../../services/catalog";
vi.mock("../../services/api", async original => ({ ...await original<object>(),
  listBackgroundSessions: vi.fn(), listAgentRuns: vi.fn(), getBackgroundSession: vi.fn(), continueInChat: vi.fn(), cancelAgentRun: vi.fn(), decideAgentRunApproval: vi.fn()
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
  vi.mocked(getBackgroundSession).mockResolvedValue(fixtureBackground);
  vi.mocked(continueInChat).mockResolvedValue({ sessionId: fixtureBackground.session.sessionId });
  vi.mocked(openCatalogSession).mockResolvedValue("ready");
});
const show = (instanceId = "instance-1") => render(<App><BackgroundWorkDrawer instanceId={instanceId} open wide onClose={() => undefined} /></App>);
describe("Background Sessions", () => {
  it("dismisses with Escape inside the drawer without reaching its parent", async () => {
    const close = vi.fn(); const parentKeyDown = vi.fn();
    render(<div onKeyDown={parentKeyDown}><App><BackgroundWorkDrawer instanceId="instance-1" open wide onClose={close} /></App></div>);
    const history = await screen.findByRole("button", { name: "View history" });
    history.focus(); fireEvent.keyDown(history, { key: "Escape" });
    expect(close).toHaveBeenCalledOnce();
    expect(parentKeyDown).not.toHaveBeenCalled();
  });
  it("opens a Session's run history and preserves bounded reading regions", async () => {
    show(); fireEvent.click(await screen.findByRole("button", { name: "View history" }));
    expect(await screen.findByText("The background check finished.")).toBeInTheDocument();
    expect(listAgentRuns).toHaveBeenCalledWith("background-1", undefined);
    expect(screen.getByRole("region", { name: "Response" })).toHaveAttribute("tabindex", "0");
    fireEvent.click(screen.getByRole("button", { name: "All background Sessions" }));
    expect(screen.getByRole("button", { name: "Progress check" })).toBeInTheDocument();
  });
  it("moves focus into history and returns to the control that opened it", async () => {
    show();
    for (const label of ["View history", "Progress check"]) {
      const opener = await screen.findByRole("button", { name: label });
      opener.focus(); fireEvent.click(opener);
      const back = await screen.findByRole("button", { name: "All background Sessions" });
      expect(back).toHaveFocus();
      fireEvent.click(back);
      expect(screen.getByRole("button", { name: label })).toHaveFocus();
    }
  });
  it("continues the same Session in chat and refreshes the chat rail", async () => {
    show(); fireEvent.click(await screen.findByRole("button", { name: "Continue in chat" }));
    await waitFor(() => expect(openCatalogSession).toHaveBeenCalledWith(fixtureBackground.session));
    expect(continueInChat).toHaveBeenCalledWith("background-1"); expect(refreshCatalog).toHaveBeenCalledWith(true);
  });
  it.each([
    ["failed", "The chat connection could not be opened. Try Continue in chat again."],
    ["blocked", "This Session is archived. Refresh to update its availability."]
  ] as const)("keeps a %s chat opening visible and allows recovery", async (result, message) => {
    vi.mocked(openCatalogSession).mockResolvedValueOnce(result).mockResolvedValueOnce("ready");
    const close = vi.fn();
    render(<App><BackgroundWorkDrawer instanceId="instance-1" open wide onClose={close} /></App>);
    fireEvent.click(await screen.findByRole("button", { name: "Continue in chat" }));
    expect(await screen.findByText(message)).toBeInTheDocument();
    expect(close).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Continue in chat" }));
    await waitFor(() => expect(close).toHaveBeenCalledOnce());
    expect(screen.queryByText(message)).not.toBeInTheDocument();
  });
  it("reveals the read-only chat if the Session ends while opening it", async () => {
    vi.mocked(openCatalogSession).mockResolvedValue("ended");
    const close = vi.fn();
    render(<App><BackgroundWorkDrawer instanceId="instance-1" open wide onClose={close} /></App>);
    fireEvent.click(await screen.findByRole("button", { name: "Continue in chat" }));
    await waitFor(() => expect(close).toHaveBeenCalledOnce());
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
    expect(await screen.findByRole("button", { name: "Progress check" })).toBeInTheDocument();
  });
  it("uses the server cursor for bounded pagination", async () => {
    vi.mocked(listBackgroundSessions).mockResolvedValueOnce({ items: [fixtureBackground], nextCursor: "opaque-cursor", hasMore: true });
    vi.mocked(listBackgroundSessions).mockResolvedValueOnce({ items: [{ ...fixtureBackground, session: { ...fixtureBackground.session, sessionId: "background-2", title: "Another task" } }], nextCursor: null, hasMore: false });
    show(); fireEvent.click(await screen.findByRole("button", { name: "Load more" }));
    await waitFor(() => expect(listBackgroundSessions).toHaveBeenCalledWith("instance-1", "opaque-cursor"));
  });
  it("preserves archive boundaries and quiet NoAction", async () => {
    vi.mocked(listBackgroundSessions).mockResolvedValue({ items: [{ ...fixtureBackground, canContinueInChat: false }], nextCursor: null, hasMore: false });
    vi.mocked(listAgentRuns).mockResolvedValue({ items: [{ ...fixtureRun, outcome: { kind: "NoAction", summary: "", outcomeEntryId: null, attentionRequired: false } }], nextCursor: null, hasMore: false });
    show(); expect(await screen.findByRole("button", { name: "Continue in chat" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "View history" }));
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
