import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { App as AntApp } from "antd";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { WorkItem, WorkItemResult } from "../../services/api";
import { BackgroundWorkDrawer } from "./BackgroundWorkDrawer";
import { formatChatTime } from "./chatTime";

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

const retrying: WorkItem = {
  ...queued,
  workItemId: "work-retry",
  status: "retrying",
  progress: null,
  failureCode: "empty-result",
  failureSummary: "The model returned no result.",
  attemptCount: 2,
  maxAttempts: 3
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
    expect(await screen.findByText("No runs yet. Runs appear when schedules, thoughts, events, or retrospection execute.")).toBeInTheDocument();

  });

  it("shows the retry attempt and the last safe reason", async () => {
    renderDrawer(async () => [retrying]);
    expect(await screen.findByText("Retrying · attempt 2 of 3")).toBeInTheDocument();
    expect(screen.getByText("The model returned no result.")).toBeInTheDocument();
  });

  it("closes when Escape is pressed inside the drawer", async () => {
    const onClose = vi.fn();
    render(
      <AntApp>
        <BackgroundWorkDrawer
          sessionId="session-1"
          open
          wide
          refreshKey={0}
          pollIntervalMs={60_000}
          onClose={onClose}
          load={async () => []}
          loadResult={async () => ({ workItemId: "", text: "", completedAt: "" })}
          cancel={vi.fn()}
          approve={vi.fn()}
          reject={vi.fn()}
        />
      </AntApp>
    );
    expect(await screen.findByText("No runs yet. Runs appear when schedules, thoughts, events, or retrospection execute.")).toBeInTheDocument();
    fireEvent.keyDown(screen.getByRole("dialog", { name: "Background work" }), { key: "Escape" });
    expect(onClose).toHaveBeenCalledOnce();
  });

  it("closes when Escape is pressed while focus stays outside the drawer", async () => {
    const onClose = vi.fn();
    render(
      <AntApp>
        <BackgroundWorkDrawer
          sessionId="session-1"
          open
          wide
          refreshKey={0}
          pollIntervalMs={60_000}
          onClose={onClose}
          load={async () => []}
          loadResult={async () => ({ workItemId: "", text: "", completedAt: "" })}
          cancel={vi.fn()}
          approve={vi.fn()}
          reject={vi.fn()}
        />
      </AntApp>
    );
    expect(await screen.findByText("No runs yet. Runs appear when schedules, thoughts, events, or retrospection execute.")).toBeInTheDocument();
    fireEvent.keyDown(document.body, { key: "Escape" });
    // The capture listener and Ant Design's portal Escape handler both close.
    expect(onClose).toHaveBeenCalledTimes(2);
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
    expect(screen.getAllByText("Schedule").length).toBeGreaterThan(0);
    expect(screen.getByText("Event")).toBeInTheDocument();
    expect(screen.getByText("Queued")).toBeInTheDocument();
    expect(screen.queryByText("Needs attention")).not.toBeInTheDocument();
    expect(screen.getByText("Needs approval")).toBeInTheDocument();
    expect(screen.getAllByText("Completed")[0]).toBeInTheDocument();
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
    fireEvent.click(await screen.findByRole("button", { name: "Cancel Schedule" }));
    fireEvent.click(await screen.findByRole("button", { name: "Cancel work" }));
    await waitFor(() => expect(cancel).toHaveBeenCalledWith("session-1", queued.workItemId, 1));
    expect(await screen.findByText("Cancelled")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Approve Event" }));
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
    expect(await screen.findByText("No runs yet. Runs appear when schedules, thoughts, events, or retrospection execute.")).toBeInTheDocument();
    expect(document.querySelector(".ant-drawer-content-wrapper")).toHaveStyle({ width: "400px" });
    wide.unmount();

    renderDrawer(async () => [], { wide: false });
    expect(await screen.findByText("No runs yet. Runs appear when schedules, thoughts, events, or retrospection execute.")).toBeInTheDocument();
    expect(document.querySelector(".ant-drawer-content-wrapper")).toHaveStyle({ width: "320px" });
  });

  it("reuses a slow result request across polls until it completes", async () => {
    vi.useFakeTimers();
    let release: (result: WorkItemResult) => void = () => undefined;
    const pending = new Promise<WorkItemResult>(resolve => { release = resolve; });
    const load = vi.fn().mockResolvedValue([completed]);
    const loadResult = vi.fn().mockReturnValue(pending);
    renderDrawer(load, { loadResult, pollIntervalMs: 1000 });
    await act(async () => { await Promise.resolve(); });
    expect(loadResult).toHaveBeenCalledTimes(1);
    await act(() => vi.advanceTimersByTimeAsync(3000));
    expect(loadResult).toHaveBeenCalledTimes(1);
    await act(async () => { release({ workItemId: completed.workItemId, text: "Slow result delivered", completedAt: completed.updatedAt }); });
    expect(screen.getByText("Slow result delivered")).toBeInTheDocument();
  });

  it("does not show an old owner's cancellation failure after switching scope", async () => {
    let reject: (reason: Error) => void = () => undefined;
    const pending = new Promise<WorkItem>((_resolve, failure) => { reject = failure; });
    const cancel = vi.fn().mockReturnValue(pending);
    const load = vi.fn(async (owner: string) => [owner === "session-1" ? queued : { ...completed, origin: "Other owner's work" }]);
    const view = renderDrawer(load, { cancel });
    fireEvent.click(await screen.findByRole("button", { name: "Cancel Schedule" }));
    fireEvent.click(await screen.findByRole("button", { name: "Cancel work" }));
    await waitFor(() => expect(cancel).toHaveBeenCalledTimes(1));
    view.rerender(<AntApp><BackgroundWorkDrawer sessionId="session-2" open wide onClose={() => undefined}
      load={load} loadResult={async () => ({ workItemId: "", text: "", completedAt: "" })}
      cancel={cancel} approve={vi.fn()} reject={vi.fn()} /></AntApp>);
    await screen.findByText("Other owner's work");
    await act(async () => { reject(new Error("Old owner cancellation failed")); });
    expect(screen.queryByText("Old owner cancellation failed")).not.toBeInTheDocument();
  });

  it("polls only while open and queues refresh behind a slow response", async () => {
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
    expect(load).toHaveBeenCalledTimes(1);

    await act(async () => {
      resolveFirst([older]);
      await Promise.resolve();
    });
    expect(load).toHaveBeenCalledTimes(2);
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

  it("labels attention results and leaves quiet completions unlabeled", async () => {
    const attention = { ...completed, workItemId: "work-attention", origin: "Morning review", attentionRequired: true };
    const quiet = { ...completed, workItemId: "work-quiet", origin: "Quiet check", attentionRequired: false };
    renderDrawer(async () => [attention, quiet], {
      loadResult: async (_sessionId, workItemId) => ({
        workItemId,
        text: workItemId === attention.workItemId ? "Two orders need review." : "Nothing to report.",
        completedAt: completed.updatedAt,
        attentionRequired: workItemId === attention.workItemId
      })
    });
    expect(await screen.findByText("Needs attention")).toBeInTheDocument();
    expect(screen.getAllByText("Needs attention")).toHaveLength(1);
    expect(await screen.findByText("Two orders need review.")).toBeInTheDocument();
    expect(await screen.findByText("Nothing to report.")).toBeInTheDocument();
  });

  it("shows scheduled and order-placed sources without webhook evidence", async () => {
    const placed = {
      ...completed,
      workItemId: "work-placed",
      origin: "Order placed",
      updatedAt: "2026-10-04T01:00:00.000Z"
    };
    renderDrawer(async () => [queued, placed], {
      loadResult: async () => ({ workItemId: "", text: "", completedAt: "" })
    });
    expect(await screen.findByText("Schedule")).toBeInTheDocument();
    expect(screen.getByText("Event")).toBeInTheDocument();
    const source = screen.getByLabelText("Source: Event");
    const updated = [...source.closest("li")!.querySelectorAll("time")].find(time => time.parentElement?.textContent?.startsWith("Updated"));
    expect(updated).toHaveAttribute("dateTime", placed.updatedAt);
    expect(updated).toHaveTextContent(formatChatTime(placed.updatedAt) ?? placed.updatedAt);
    expect(screen.queryByText(/sourceEventId|orderReference|\{/)).not.toBeInTheDocument();
  });
  it("opens a specific older run outside the first page and links to its exact source", async () => {
    Object.defineProperty(HTMLElement.prototype, "scrollIntoView", { configurable: true, value: vi.fn() });
    const onSource = vi.fn();
    const target = { ...completed, workItemId: "older-run", registrationId: "original-schedule" };
    const loadOne = vi.fn().mockResolvedValue(target);
    render(<AntApp><BackgroundWorkDrawer sessionId="instance" open inline wide selectedWorkItemId="older-run"
      onClose={vi.fn()} load={async () => [queued]} loadOne={loadOne}
      loadResult={async () => ({ workItemId: target.workItemId, text: "Original result", completedAt: target.updatedAt })}
      cancel={vi.fn()} approve={vi.fn()} reject={vi.fn()} onSource={onSource} /></AntApp>);
    expect(await screen.findByText("Original result")).toBeVisible();
    expect(loadOne).toHaveBeenCalledWith("instance", "older-run");
    const selected = document.querySelector('[data-work-item-id="older-run"]')!;
    expect(selected).toHaveClass("background-work-selected");
    fireEvent.click(screen.getByRole("button", { name: "View schedule" }));
    expect(onSource).toHaveBeenCalledWith({ kind: "schedule", registrationId: "original-schedule" });
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("shows actionable failure when a selected run cannot be read", async () => {
    const loadOne = vi.fn().mockRejectedValue(new Error("Run is no longer available."));
    render(<AntApp><BackgroundWorkDrawer sessionId="instance" open inline wide selectedWorkItemId="missing-run"
      onClose={vi.fn()} load={async () => []} loadOne={loadOne}
      loadResult={vi.fn()} cancel={vi.fn()} approve={vi.fn()} reject={vi.fn()} /></AntApp>);
    expect(await screen.findByText("Run is no longer available.")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Retry selected run" }));
    await waitFor(() => expect(loadOne).toHaveBeenCalledTimes(2));
  });

  it("exposes a failed result read and retries without changing the run", async () => {
    const loadResult = vi.fn().mockRejectedValueOnce(new Error("Result storage temporarily unavailable"))
      .mockResolvedValue({ workItemId: completed.workItemId, text: "Recovered result", completedAt: completed.updatedAt });
    renderDrawer(async () => [completed], { loadResult });
    expect(await screen.findByText("Result storage temporarily unavailable")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Retry result" }));
    expect(await screen.findByText("Recovered result")).toBeVisible();
    expect(screen.queryByText("Result storage temporarily unavailable")).not.toBeInTheDocument();
    expect(loadResult).toHaveBeenCalledTimes(2);
  });

  it("disables every owner action while a run mutation is pending", async () => {
    let finish!: (item: WorkItem) => void;
    const cancel = vi.fn(() => new Promise<WorkItem>(resolve => { finish = resolve; }));
    renderDrawer(async () => [queued, approval], { cancel });
    fireEvent.click(await screen.findByRole("button", { name: "Cancel Schedule" }));
    fireEvent.click(await screen.findByRole("button", { name: "Cancel work" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Approve Event" })).toBeDisabled());
    await act(async () => { finish({ ...queued, revision: 2, status: "cancelled", cancellationAvailable: false }); });
    expect(screen.getByRole("button", { name: "Approve Event" })).toBeEnabled();
  });

});
