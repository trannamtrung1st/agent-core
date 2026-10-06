import { App, ConfigProvider } from "antd";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { InstanceSchedulesSection } from "./InstanceSchedulesSection";
import { instanceContinuityRequest, type OwnerSchedule } from "../../services/adminApi";
import { listModels } from "../../services/api";
vi.mock("../../services/adminApi", () => ({ instanceContinuityRequest: vi.fn() }));
vi.mock("../../services/api", () => ({ listModels: vi.fn() }));
const request = vi.mocked(instanceContinuityRequest);
const row: OwnerSchedule = { registrationId: "scheduled", revision: 2, intent: "Review store orders", enabled: true, status: "Active",
  schedule: { kind: "daily", interval: 1, localTime: "09:00", timeZone: "UTC" }, authorizationOrigin: "CurrentUserTurn",
  sourceSessionId: "source-session", sourceEventId: null, createdAt: "2026-10-05T00:00:00Z", nextRunAt: "2026-10-06T09:00:00Z",
  modelKey: null, reasoningEffort: null, effectiveModelKey: "scripted-alpha", lastWorkItemId: null, executionStatus: null };
const view = () => <ConfigProvider><App><InstanceSchedulesSection instanceId="instance" onWork={vi.fn()} /></App></ConfigProvider>;
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
beforeEach(() => {
  vi.clearAllMocks(); vi.mocked(listModels).mockResolvedValue({ defaultKey: "scripted-alpha", models: [] });
  request.mockResolvedValue({ items: [] });
});
describe("Owner schedule authoring", () => {
  it.each([["WaitingForApproval", "Needs approval"], ["WaitingToRetry", "Retrying"]])("uses readable run state for %s", async (executionStatus, label) => {
    request.mockResolvedValue({ items: [{ ...row, executionStatus, lastWorkItemId: "last-run" }] });
    render(view());
    await screen.findByRole("button", { name: `View schedule: ${row.intent}` });
    fireEvent.click(screen.getByRole("button", { name: `View schedule: ${row.intent}` }));
    expect(screen.getByRole("region", { name: "Schedule details" })).toHaveTextContent(label);
    expect(screen.getByRole("button", { name: "Run schedule now" })).toBeDisabled();
  });
  it("refreshes a source created after the cached review when returning from Runs", async () => {
    Object.defineProperty(HTMLElement.prototype, "scrollIntoView", { configurable: true, value: vi.fn() });
    request.mockResolvedValue({ items: [] });
    const ui = (selection?: { kind: "schedule"; registrationId: string; request: number }) => <ConfigProvider><App>
      <InstanceSchedulesSection instanceId="instance" onWork={vi.fn()} selection={selection} /></App></ConfigProvider>;
    const mounted = render(ui());
    await screen.findByText(/No schedules yet/);
    request.mockResolvedValue({ items: [row] });
    mounted.rerender(ui({ kind: "schedule", registrationId: row.registrationId, request: 1 }));
    const source = await screen.findByRole("button", { name: `View schedule: ${row.intent}` });
    await waitFor(() => expect(source).toHaveFocus());
    expect(source).toHaveAttribute("aria-expanded", "true");
    expect(screen.queryByText("This source configuration is no longer available")).not.toBeInTheDocument();
  });
  it.each(["Disabled", "Cancelled", "Completed", "Expired"])("does not advertise a future run for %s registrations", async status => {
    request.mockResolvedValue({ items: [{ ...row, enabled: status !== "Disabled", status }] });
    render(view());
    expect(await screen.findByText("Not scheduled")).toBeVisible();
    expect(screen.queryByText(/2026/)).not.toBeInTheDocument();
  });
  it("resets pagination when searching provenance and preserves the selected registration revision", async () => {
    const rows = Array.from({ length: 11 }, (_, index) => ({ ...row, registrationId: `schedule-${index}`, revision: index + 2,
      intent: `Task ${index}`, sourceSessionId: index === 10 ? "unique-chat-source" : "source-session" }));
    request.mockResolvedValue({ items: rows }); render(view());
    const section = screen.getByRole("region", { name: "Schedules" });
    await within(section).findByRole("button", { name: "View schedule: Task 0" });
    fireEvent.click(section.querySelector('.ant-pagination-next button')!);
    expect(await within(section).findByRole("button", { name: "View schedule: Task 10" })).toBeVisible();
    fireEvent.change(within(section).getByRole("textbox", { name: "Search schedules" }), { target: { value: "unique-chat-source" } });
    expect(await within(section).findByText("1 results")).toBeVisible();
    fireEvent.click(within(section).getByRole("button", { name: "View schedule: Task 10" }));
    expect(within(section).getByRole("region", { name: "Schedule details" })).toHaveTextContent("unique-chat-source");
    fireEvent.click(within(section).getByRole("button", { name: "Disable schedule" }));
    await waitFor(() => expect(request).toHaveBeenCalledWith("instance", "schedules/schedule-10", "PUT",
      expect.objectContaining({ expectedRevision: 12, enabled: false })));
  });
  it("creates a structured schedule without model interpretation", async () => {
    render(view()); await screen.findByText(/No schedules yet/);
    fireEvent.click(screen.getByRole("button", { name: "New schedule" }));
    expect(screen.getByRole("button", { name: "Create schedule" })).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Schedule task"), { target: { value: "Review pending orders" } });
    fireEvent.change(screen.getByLabelText("Schedule time zone"), { target: { value: "Asia/Ho_Chi_Minh" } });
    fireEvent.click(screen.getByRole("button", { name: "Create schedule" }));
    await waitFor(() => expect(request).toHaveBeenCalledWith("instance", "schedules", "POST", {
      expectedRevision: 0, enabled: true, intent: "Review pending orders", modelKey: null, reasoningEffort: null,
      schedule: { kind: "daily", timeZone: "Asia/Ho_Chi_Minh", interval: 1, localTime: "09:00" }
    }));
  });
  it("requires a finite bound under Definition policy and sends the selected occurrence limit", async () => {
    request.mockResolvedValue({ items: [], policy: { allowOneShot: true, allowDaily: true, allowWeekly: true, allowFixedInterval: true,
      allowIndefiniteRecurrence: false, oneShotHorizonDays: 10, minRecurrenceDays: 1, minFixedIntervalSeconds: 300, maxActiveRegistrations: 2 } });
    render(view()); await screen.findByText(/No schedules yet/);
    fireEvent.click(screen.getByRole("button", { name: "New schedule" }));
    fireEvent.change(screen.getByLabelText("Schedule task"), { target: { value: "Finite store audit" } });
    expect(screen.getByText(/This Definition requires an end date/)).toBeVisible();
    expect(screen.getByRole("button", { name: "Create schedule" })).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Schedule maximum occurrences"), { target: { value: "3" } });
    fireEvent.click(screen.getByRole("button", { name: "Create schedule" }));
    await waitFor(() => expect(request).toHaveBeenCalledWith("instance", "schedules", "POST", expect.objectContaining({
      schedule: expect.objectContaining({ maxOccurrences: 3 })
    })));
  });
  it("preserves chat provenance and keeps accepted run locked across stale status until a new execution", async () => {
    let current = row; request.mockImplementation(async () => ({ items: [current] }));
    render(view()); fireEvent.click(await screen.findByText(row.intent));
    expect(screen.getByText(/Chat user request.*source-session/)).toBeVisible();
    expect(screen.getByText("Originally created from")).toBeVisible();
    const run = screen.getByRole("button", { name: "Run schedule now" });
    await act(async () => { run.click(); run.click(); });
    await waitFor(() => expect(request.mock.calls.filter(c => c[1].endsWith("/run"))).toHaveLength(1));
    expect(run).toBeDisabled(); expect(run).toHaveTextContent("Starting…");
    current = { ...row, lastWorkItemId: "new-run", executionStatus: "Completed" };
    fireEvent.click(screen.getByRole("button", { name: "Refresh schedules" }));
    await waitFor(() => expect(run).toBeEnabled());
  });
  it("releases a rejected run and does not automatically replay a stale mutation", async () => {
    request.mockImplementation(async (_id, path) => { if (path.endsWith("/run")) throw new Error("Schedule revision is stale."); return { items: [row] }; });
    render(view()); fireEvent.click(await screen.findByText(row.intent));
    fireEvent.click(screen.getByRole("button", { name: "Run schedule now" }));
    expect(await screen.findByText("Schedule revision is stale.")).toBeVisible();
    expect(screen.getByRole("button", { name: "Run schedule now" })).toBeEnabled();
    expect(request.mock.calls.filter(c => c[1].endsWith("/run"))).toHaveLength(1);
  });
  it("edits and disables through the same revisioned registration", async () => {
    request.mockResolvedValue({ items: [row] }); render(view());
    fireEvent.click(await screen.findByText(row.intent)); fireEvent.click(screen.getByRole("button", { name: "Edit schedule" }));
    fireEvent.change(screen.getByLabelText("Schedule task"), { target: { value: "Future orders" } });
    fireEvent.click(screen.getByRole("switch", { name: "Enable schedule" }));
    fireEvent.click(screen.getByRole("button", { name: "Save schedule" }));
    await waitFor(() => expect(request).toHaveBeenCalledWith("instance", "schedules/scheduled", "PUT", {
      expectedRevision: 2, enabled: false, intent: "Future orders", schedule: row.schedule, modelKey: null, reasoningEffort: null
    }));
  });
  it("opens the exact source beyond pagination without discarding an editor draft", async () => {
    Object.defineProperty(HTMLElement.prototype, "scrollIntoView", { configurable: true, value: vi.fn() });
    const rows = Array.from({ length: 12 }, (_, index) => ({ ...row, registrationId: `schedule-${index}`, intent: `Task ${index}`, lastWorkItemId: `run-${index}` }));
    request.mockResolvedValue({ items: rows });
    const onWork = vi.fn();
    const ui = (selection?: { kind: "schedule"; registrationId: string; request: number }) => <ConfigProvider><App>
      <InstanceSchedulesSection instanceId="instance" onWork={onWork} selection={selection} /></App></ConfigProvider>;
    const view = render(ui());
    await screen.findByRole("button", { name: "View schedule: Task 0" });
    fireEvent.click(screen.getByRole("button", { name: "New schedule" }));
    fireEvent.change(screen.getByLabelText("Schedule task"), { target: { value: "Unsaved task" } });
    fireEvent.change(screen.getByRole("textbox", { name: "Search schedules" }), { target: { value: "Task 0" } });
    view.rerender(ui({ kind: "schedule", registrationId: "schedule-11", request: 1 }));
    const source = await screen.findByRole("button", { name: "View schedule: Task 11" });
    await waitFor(() => expect(source).toHaveAttribute("aria-expanded", "true"));
    expect(screen.getByLabelText("Schedule task")).toHaveValue("Unsaved task");
    expect(screen.getByRole("textbox", { name: "Search schedules" })).toHaveValue("");
    expect(screen.getByText("Originally created from")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "View last run" }));
    expect(onWork).toHaveBeenCalledWith("run-11");
  });

  it("pauses polling while hidden and retains the unsaved task when shown", async () => {
    const ui = (active: boolean) => <App><InstanceSchedulesSection instanceId="instance" active={active} onWork={vi.fn()} /></App>;
    const mounted = render(ui(true));
    await screen.findByText(/No schedules yet/);
    fireEvent.click(screen.getByRole("button", { name: "New schedule" }));
    fireEvent.change(screen.getByLabelText("Schedule task"), { target: { value: "Retained draft" } });
    mounted.rerender(ui(false));
    const before = request.mock.calls.length;
    vi.useFakeTimers();
    try { await act(async () => { await vi.advanceTimersByTimeAsync(10000); }); }
    finally { vi.useRealTimers(); }
    expect(request.mock.calls.length).toBe(before);
    mounted.rerender(ui(true));
    await waitFor(() => expect(request.mock.calls.length).toBeGreaterThan(before));
    expect(screen.getByLabelText("Schedule task")).toHaveValue("Retained draft");
  });

});
