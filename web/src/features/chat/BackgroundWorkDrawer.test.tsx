import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { App as AntApp } from "antd";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { WorkItem, WorkItemResult } from "../../services/api";
import { BackgroundWorkDrawer } from "./BackgroundWorkDrawer";

const queued: WorkItem = {
  workItemId: "work-queued",
  status: "queued",
  revision: 1,
  origin: "Scheduled reminder",
  progress: "Waiting to start",
  needsApproval: false,
  approvalId: null,
  approvalRevision: null,
  approvalPreview: null,
  actionHash: null,
  cancellationAvailable: true,
  failureCode: null,
  failureSummary: null,
  knownEffect: null,
  createdAt: "2026-09-24T09:00:00.000Z",
  updatedAt: "2026-09-24T09:00:00.000Z"
};

const approval: WorkItem = {
  ...queued,
  workItemId: "work-approval",
  status: "needsApproval",
  revision: 4,
  origin: "Application event",
  progress: null,
  needsApproval: true,
  approvalId: "approval-1",
  approvalRevision: 1,
  approvalPreview: "POST https://example.com/items",
  actionHash: "a".repeat(64),
  cancellationAvailable: true
};

const completed: WorkItem = {
  ...queued,
  workItemId: "work-done",
  status: "completed",
  revision: 5,
  progress: "Checking the oven",
  cancellationAvailable: false
};

const failed: WorkItem = {
  ...queued,
  workItemId: "work-failed",
  status: "failed",
  progress: null,
  cancellationAvailable: false,
  failureCode: "model-failed",
  failureSummary: "The model failed."
};

function renderDrawer(
  load: (sessionId: string) => Promise<WorkItem[]>,
  options: {
    wide?: boolean;
    open?: boolean;
    refreshKey?: number;
    pollIntervalMs?: number;
    loadResult?: (sessionId: string, workItemId: string) => Promise<WorkItemResult>;
    cancel?: ReturnType<typeof vi.fn>;
    approve?: ReturnType<typeof vi.fn>;
    reject?: ReturnType<typeof vi.fn>;
  } = {}
) {
  return render(
    <AntApp>
      <BackgroundWorkDrawer
        sessionId="session-1"
        open={options.open ?? true}
        wide={options.wide ?? true}
        refreshKey={options.refreshKey ?? 0}
        pollIntervalMs={options.pollIntervalMs ?? 60_000}
        onClose={() => undefined}
        load={load}
        loadResult={options.loadResult ?? (async () => ({ workItemId: "", text: "", completedAt: "" }))}
        cancel={options.cancel ?? vi.fn()}
        approve={options.approve ?? vi.fn()}
        reject={options.reject ?? vi.fn()}
      />
    </AntApp>
  );
}

describe("BackgroundWorkDrawer", () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it("shows an empty list", async () => {
    renderDrawer(async () => []);
    expect(await screen.findByText("No background work")).toBeInTheDocument();
  });

  it("shows a load error", async () => {
    renderDrawer(async () => {
      throw new Error("Unable to load background work.");
    });
    expect(await screen.findByText("Unable to load background work.")).toBeInTheDocument();
  });

  it("shows origin, status, progress, approval, failure, and result without private terms", async () => {
    const loadResult = vi.fn().mockResolvedValue({
      workItemId: completed.workItemId,
      text: "Oven timer finished.",
      completedAt: completed.updatedAt
    });
    renderDrawer(async () => [queued, approval, completed, failed], { loadResult });
    expect(await screen.findByText("Oven timer finished.")).toBeInTheDocument();
    expect(screen.getAllByText("Scheduled reminder").length).toBeGreaterThan(0);
    expect(screen.getByText("Application event")).toBeInTheDocument();
    expect(screen.getByText("Queued")).toBeInTheDocument();
    expect(screen.getByText("Needs approval")).toBeInTheDocument();
    expect(screen.getByText("Completed")).toBeInTheDocument();
    expect(screen.getByText("Failed")).toBeInTheDocument();
    expect(screen.getByText("Waiting to start")).toBeInTheDocument();
    expect(screen.getByText("Checking the oven")).toBeInTheDocument();
    expect(screen.getByText("Result")).toBeInTheDocument();
    expect(screen.getByText("POST https://example.com/items")).toBeInTheDocument();
    expect(screen.getByText("The model failed.")).toBeInTheDocument();
    expect(screen.queryByText("model-failed")).not.toBeInTheDocument();
    expect(screen.queryByText(/checkpoint|evidence|lease/i)).not.toBeInTheDocument();
    expect(loadResult).toHaveBeenCalledWith("session-1", completed.workItemId);
  });

  it("confirms cancel, approve, and reject", async () => {
    const cancel = vi.fn().mockResolvedValue({ ...queued, status: "cancelled", cancellationAvailable: false });
    const approve = vi.fn().mockResolvedValue({
      ...approval,
      status: "queued",
      needsApproval: false,
      approvalId: null,
      approvalPreview: null,
      actionHash: null
    });
    const reject = vi.fn().mockResolvedValue({
      ...approval,
      workItemId: "work-reject",
      status: "queued",
      needsApproval: false,
      approvalId: null,
      approvalPreview: null,
      actionHash: null
    });
    renderDrawer(async () => [queued, approval, { ...approval, workItemId: "work-reject", origin: "Second event" }], {
      cancel,
      approve,
      reject
    });
    fireEvent.click(await screen.findByRole("button", { name: "Cancel Scheduled reminder" }));
    fireEvent.click(await screen.findByRole("button", { name: "Cancel work" }));
    await waitFor(() => expect(cancel).toHaveBeenCalledWith("session-1", queued.workItemId, 1));
    expect(await screen.findByText("Cancelled")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Approve Application event" }));
    fireEvent.click(await screen.findByRole("button", { name: "Approve action" }));
    await waitFor(() => expect(approve).toHaveBeenCalledWith("session-1", approval.workItemId, "approval-1", 4, 1, approval.actionHash));

    const rejectButton = screen.getByRole("button", { name: "Reject Second event" });
    rejectButton.focus();
    expect(rejectButton).toHaveFocus();
    fireEvent.click(rejectButton);
    fireEvent.click(await screen.findByRole("button", { name: "Reject action" }));
    await waitFor(() => expect(reject).toHaveBeenCalled());
  });

  it("uses the wide and narrow drawer widths", async () => {
    const wide = renderDrawer(async () => [], { wide: true });
    expect(await screen.findByText("No background work")).toBeInTheDocument();
    expect(document.querySelector(".ant-drawer-content-wrapper")).toHaveStyle({ width: "400px" });
    wide.unmount();

    renderDrawer(async () => [], { wide: false });
    expect(await screen.findByText("No background work")).toBeInTheDocument();
    expect(document.querySelector(".ant-drawer-content-wrapper")).toHaveStyle({ width: "320px" });
  });

  it("polls only while open and drops a stale response", async () => {
    vi.useFakeTimers();
    let resolveFirst: (items: WorkItem[]) => void = () => undefined;
    const first = new Promise<WorkItem[]>((resolve) => {
      resolveFirst = resolve;
    });
    const newer: WorkItem = { ...queued, origin: "Newer reminder" };
    const older: WorkItem = { ...queued, origin: "Older reminder" };
    const load = vi.fn()
      .mockImplementationOnce(() => first)
      .mockResolvedValue([newer]);
    const view = renderDrawer(load, { pollIntervalMs: 1_000 });
    await act(async () => {
      await Promise.resolve();
    });
    expect(load).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(1_000);
    });
    expect(load).toHaveBeenCalledTimes(2);
    expect(screen.getByText("Newer reminder")).toBeInTheDocument();

    await act(async () => {
      resolveFirst([older]);
      await Promise.resolve();
    });
    expect(screen.queryByText("Older reminder")).not.toBeInTheDocument();
    expect(screen.getByText("Newer reminder")).toBeInTheDocument();

    view.unmount();
    const calls = load.mock.calls.length;
    renderDrawer(load, { open: false, pollIntervalMs: 1_000 });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(5_000);
    });
    expect(load).toHaveBeenCalledTimes(calls);
  });
});
