import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { getInstanceSession, listInstanceSessions, listInstanceAgentRuns, type InstanceActivitySession, type AgentRun } from "../../services/api";
import { InstanceSessionsSection } from "./InstanceActivitySection";
import { InstanceRunsSection } from "./InstanceRunsSection";
vi.mock("../../services/api", async importOriginal => ({ ...await importOriginal<object>(), getInstanceSession: vi.fn(), listInstanceSessions: vi.fn(), listInstanceAgentRuns: vi.fn() }));
vi.mock("../chat/AgentRunDetails", () => ({ SessionRunHistory: ({ sessionId }: { sessionId: string }) => <div>History {sessionId}</div>, AgentRunStatus: ({ run }: { run: AgentRun }) => <span>{run.status}</span> }));
vi.mock("../chat/SessionArtifacts", () => ({ SessionArtifacts: () => <div>Session files</div> }));
vi.mock("../chat/BackgroundWorkDrawer", () => ({ BackgroundWorkDrawer: ({ initialSessionId }: { initialSessionId: string }) => <div role="dialog">Original result {initialSessionId}</div> }));
const row = (id: string, title = "Planning notes", origin = "UserChat"): InstanceActivitySession => ({ origin, surfaces: origin === "UserChat" ? ["ChatList"] : ["BackgroundWork"], session: { sessionId: id, title, agentId: "examiner", agentVersion: 1, status: "paused", lifecycleStatus: "paused", archived: false, ended: false, workspaceOwned: false, runtimeEpoch: 1, revision: 1, createdAt: "2026-10-09T00:00:00Z", updatedAt: "2026-10-09T01:00:00Z" } });
beforeEach(() => { vi.resetAllMocks(); window.history.replaceState(null, "", "/admin/instances/instance/activity/sessions"); vi.mocked(listInstanceSessions).mockResolvedValue({ items: [], nextCursor: null, hasMore: false }); vi.mocked(getInstanceSession).mockResolvedValue(row("session")); });
describe("Instance Activity", () => {
  it("does not let a cached row hide an exact-detail failure and retries before offering actions", async () => {
    vi.mocked(listInstanceSessions).mockResolvedValue({ items: [row("session")], nextCursor: null, hasMore: false });
    vi.mocked(getInstanceSession).mockRejectedValueOnce(new Error("Session details are unavailable")).mockResolvedValueOnce(row("session"));
    render(<InstanceSessionsSection instanceId="instance" open />);
    fireEvent.click(await screen.findByRole("button", { name: "Planning notes" }));
    expect(await screen.findByText("Session details are unavailable")).toBeVisible();
    expect(screen.queryByRole("button", { name: "Open conversation" })).not.toBeInTheDocument();
    expect(screen.queryByText("History session")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry session" }));
    expect(await screen.findByText("History session")).toBeVisible();
    expect(screen.queryByText("Session details are unavailable")).not.toBeInTheDocument();
  });
  it("lists zero-run sessions by meaningful title and preserves IDs as secondary metadata", async () => {
    vi.mocked(listInstanceSessions).mockResolvedValue({ items: [row("session"), row("untitled", " "), row("task", "", "ManualBackground")], nextCursor: null, hasMore: false });
    render(<InstanceSessionsSection instanceId="instance" open />);
    expect(await screen.findByRole("button", { name: "Planning notes" })).toBeVisible();
    expect(screen.getByRole("button", { name: "Untitled conversation" })).toBeVisible();
    expect(screen.getByRole("button", { name: "Untitled background task" })).toBeVisible();
    expect(listInstanceSessions).toHaveBeenCalledWith("instance", undefined);
    fireEvent.click(screen.getByRole("button", { name: "Planning notes" }));
    expect(await screen.findByText("History session")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Open conversation" }));
    expect(window.location.pathname).toBe("/c/session");
    expect(new URLSearchParams(window.location.search).get("returnTo")).toContain("/activity/sessions");
  });
  it("retains archived Instance inspection without offering a new attachment", async () => {
    vi.mocked(listInstanceSessions).mockResolvedValue({ items: [row("session")], nextCursor: null, hasMore: false });
    render(<InstanceSessionsSection instanceId="instance" open archived />);
    fireEvent.click(await screen.findByRole("button", { name: "Planning notes" }));
    expect(await screen.findByText("History session")).toBeVisible();
    expect(screen.getByRole("button", { name: "Open conversation" })).toBeDisabled();
    expect(screen.getByText(/Archived Agent Instance/)).toBeVisible();
  });
  it("deduplicates sessions across changing cursor pages and keeps search in the URL", async () => {
    vi.mocked(listInstanceSessions).mockResolvedValueOnce({ items: [row("session")], nextCursor: "older", hasMore: true }).mockResolvedValueOnce({ items: [row("session"), row("older", "Older work")], nextCursor: null, hasMore: false });
    render(<InstanceSessionsSection instanceId="instance" open />);
    await screen.findByRole("button", { name: "Planning notes" });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Load more" })); });
    expect(screen.getAllByRole("button", { name: "Planning notes" })).toHaveLength(1);
    fireEvent.change(screen.getByRole("textbox", { name: "Search loaded sessions" }), { target: { value: "Older" } });
    expect(window.location.search).toBe("?q=Older");
    expect(screen.getByRole("button", { name: "Older work" })).toBeVisible();
    expect(screen.queryByRole("button", { name: "Planning notes" })).not.toBeInTheDocument();
  });
  it("loads an exact background deep link outside the current list page", async () => {
    window.history.replaceState(null, "", "/admin/instances/instance/activity/sessions?session=task");
    vi.mocked(getInstanceSession).mockResolvedValue(row("task", "Earlier background work", "AutomationOccurrence"));
    render(<InstanceSessionsSection instanceId="instance" open />);
    expect(await screen.findByText("Original result task")).toBeVisible();
    expect(getInstanceSession).toHaveBeenCalledWith("instance", "task");
  });
  it("offers explicit recovery for a failed page and a distinct no-results state", async () => {
    vi.mocked(listInstanceSessions).mockRejectedValueOnce(new Error("Activity unavailable")).mockResolvedValueOnce({ items: [row("session")], nextCursor: null, hasMore: false });
    render(<InstanceSessionsSection instanceId="instance" open />);
    expect(await screen.findByText("Activity unavailable")).toBeVisible();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Try again" })); });
    expect(await screen.findByRole("button", { name: "Planning notes" })).toBeVisible();
    fireEvent.change(screen.getByRole("textbox", { name: "Search loaded sessions" }), { target: { value: "missing" } });
    expect(screen.getByText(/No matching loaded sessions/)).toBeVisible();
  });
  it("ignores late Activity reads from a previous instance", async () => {
    let finish!: (value: Awaited<ReturnType<typeof listInstanceSessions>>) => void;
    vi.mocked(listInstanceSessions).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
    const view = render(<InstanceSessionsSection instanceId="previous" open />);
    view.rerender(<InstanceSessionsSection instanceId="current" open />);
    await act(async () => { finish({ items: [row("foreign", "Foreign conversation")], nextCursor: null, hasMore: false }); });
    await waitFor(() => expect(listInstanceSessions).toHaveBeenCalledWith("current", undefined));
    expect(screen.queryByRole("button", { name: "Foreign conversation" })).not.toBeInTheDocument();
  });
  it("keeps each run independently inspectable even with no conventional session metadata", async () => {
    const runs = ["UserTurn", "ScheduledWork", "ApplicationEvent", "ImmediateBackground"].map((activationKind, index) => ({ agentRunId: `run-${index}`, activationKind, sessionId: index === 3 ? "" : "shared-session", status: "completed", createdAt: "2026-10-09T00:00:00Z", updatedAt: "2026-10-09T01:00:00Z" } as AgentRun));
    vi.mocked(listInstanceAgentRuns).mockResolvedValue({ items: runs, nextCursor: null, hasMore: false });
    const onRun = vi.fn(); render(<InstanceRunsSection instanceId="instance" open inline onClose={vi.fn()} onRun={onRun} />);
    await screen.findByRole("button", { name: "Chat turn" });
    for (const [index, name] of ["Chat turn", "Scheduled task", "Application event", "Immediate task"].entries()) { fireEvent.click(screen.getByRole("button", { name })); expect(onRun).toHaveBeenLastCalledWith(`run-${index}`); }
    expect(screen.getByText("No session")).toBeVisible();
  });
});
