import { App, ConfigProvider } from "antd";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { InstanceAutomationsSection } from "./InstanceAutomationsSection";
import { instanceContinuityRequest, type Automation } from "../../services/adminApi";
import { listModels } from "../../services/api";
vi.mock("../../services/adminApi", () => ({ instanceContinuityRequest: vi.fn(), listEventSources: vi.fn(async () => []) }));
vi.mock("../../services/api", () => ({ listModels: vi.fn() }));
const request = vi.mocked(instanceContinuityRequest);
const row: Automation = { automationId: "scheduled", revision: 2, name: "Review store orders", outcome: null, instructions: "Review store orders", enabled: true, status: "Active",
  trigger: { kind: "schedule", schedule: { kind: "daily", interval: 1, localTime: "09:00", timeZone: "UTC" } }, authorizationOrigin: "CurrentUserTurn",
  sourceSessionId: "source-session", sourceEventId: null, createdAt: "2026-10-05T00:00:00Z", nextRunAt: "2026-10-06T09:00:00Z",
  modelKey: null, reasoningEffort: null, effectiveModelKey: "scripted-alpha", lastWorkItemId: null, executionStatus: null };
const view = () => <ConfigProvider><App><InstanceAutomationsSection instanceId="instance" onWork={vi.fn()} /></App></ConfigProvider>;
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
beforeEach(() => {
  vi.clearAllMocks(); vi.mocked(listModels).mockResolvedValue({ defaultKey: "scripted-alpha", models: [] });
  request.mockResolvedValue({ items: [] });
});
describe("Owner schedule authoring", () => {
  it.each([["WaitingForApproval", "Needs approval"], ["WaitingToRetry", "Retrying"]])("uses readable run state for %s", async (executionStatus, label) => {
    request.mockResolvedValue({ items: [{ ...row, executionStatus, lastWorkItemId: "last-run" }] });
    render(view());
    await screen.findByRole("button", { name: `View automation: ${row.name}` });
    fireEvent.click(screen.getByRole("button", { name: `View automation: ${row.name}` }));
    expect(screen.getByRole("region", { name: "Automation details" })).toHaveTextContent(label);
    expect(screen.getByRole("button", { name: "Run automation now" })).toBeDisabled();
  });
  it("refreshes a source created after the cached review when returning from Runs", async () => {
    Object.defineProperty(HTMLElement.prototype, "scrollIntoView", { configurable: true, value: vi.fn() });
    request.mockResolvedValue({ items: [] });
    const ui = (selection?: { kind: "automation"; automationId: string; request: number }) => <ConfigProvider><App>
      <InstanceAutomationsSection instanceId="instance" onWork={vi.fn()} selection={selection} /></App></ConfigProvider>;
    const mounted = render(ui());
    await screen.findByText(/No automations yet/);
    request.mockResolvedValue({ items: [row] });
    mounted.rerender(ui({ kind: "automation", automationId: row.automationId, request: 1 }));
    const source = await screen.findByRole("button", { name: `View automation: ${row.name}` });
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
    const rows = Array.from({ length: 11 }, (_, index) => ({ ...row, automationId: `schedule-${index}`, revision: index + 2,
      name: `Task ${index}`, instructions: `Task ${index}`, sourceSessionId: index === 10 ? "unique-chat-source" : "source-session" }));
    request.mockResolvedValue({ items: rows }); render(view());
    const section = screen.getByRole("region", { name: "Automations" });
    await within(section).findByRole("button", { name: "View automation: Task 0" });
    fireEvent.click(section.querySelector('.ant-pagination-next button')!);
    expect(await within(section).findByRole("button", { name: "View automation: Task 10" })).toBeVisible();
    fireEvent.change(within(section).getByRole("textbox", { name: "Search automations" }), { target: { value: "unique-chat-source" } });
    expect(await within(section).findByText("1 results")).toBeVisible();
    fireEvent.click(within(section).getByRole("button", { name: "View automation: Task 10" }));
    expect(within(section).getByRole("region", { name: "Automation details" })).toHaveTextContent("unique-chat-source");
    fireEvent.click(within(section).getByRole("button", { name: "Disable automation" }));
    await waitFor(() => expect(request).toHaveBeenCalledWith("instance", "automations/schedule-10", "PUT",
      expect.objectContaining({ expectedRevision: 12, enabled: false })));
  });
  it("creates a structured schedule without model interpretation", async () => {
    render(view()); await screen.findByText(/No automations yet/);
    fireEvent.click(screen.getByRole("button", { name: "New automation" }));
    expect(screen.getByRole("button", { name: "Create automation" })).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Automation name"), { target: { value: "Review orders" } });
    fireEvent.change(screen.getByLabelText("Automation instructions"), { target: { value: "Review pending orders" } });
    fireEvent.change(screen.getByLabelText("Schedule time zone"), { target: { value: "Asia/Ho_Chi_Minh" } });
    fireEvent.click(screen.getByRole("button", { name: "Create automation" }));
    await waitFor(() => expect(request).toHaveBeenCalledWith("instance", "automations", "POST", {
      expectedRevision: 0, enabled: true, name: "Review orders", instructions: "Review pending orders", modelKey: null, reasoningEffort: null,
      trigger: { kind: "schedule", schedule: { kind: "daily", timeZone: "Asia/Ho_Chi_Minh", interval: 1, localTime: "09:00" } }
    }));
  });
  it("requires a finite bound under Definition policy and sends the selected occurrence limit", async () => {
    request.mockResolvedValue({ items: [], policy: { allowOneShot: true, allowDaily: true, allowWeekly: true, allowFixedInterval: true,
      allowIndefiniteRecurrence: false, oneShotHorizonDays: 10, minRecurrenceDays: 1, minFixedIntervalSeconds: 300, maxActiveRegistrations: 2 } });
    render(view()); await screen.findByText(/No automations yet/);
    fireEvent.click(screen.getByRole("button", { name: "New automation" }));
    fireEvent.change(screen.getByLabelText("Automation name"), { target: { value: "Audit" } });
    fireEvent.change(screen.getByLabelText("Automation instructions"), { target: { value: "Finite store audit" } });
    expect(screen.getByText(/This Definition requires an end date/)).toBeVisible();
    expect(screen.getByRole("button", { name: "Create automation" })).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Schedule maximum occurrences"), { target: { value: "3" } });
    fireEvent.click(screen.getByRole("button", { name: "Create automation" }));
    await waitFor(() => expect(request).toHaveBeenCalledWith("instance", "automations", "POST", expect.objectContaining({
      trigger: { kind: "schedule", schedule: expect.objectContaining({ maxOccurrences: 3 }) }
    })));
  });
  it("preserves chat provenance and keeps accepted run locked across stale status until a new execution", async () => {
    let current = row; request.mockImplementation(async () => ({ items: [current] }));
    render(view()); fireEvent.click(await screen.findByText(row.name));
    expect(screen.getByText(/Chat user request.*source-session/)).toBeVisible();
    expect(screen.getByText("Originally created from")).toBeVisible();
    const run = screen.getByRole("button", { name: "Run automation now" });
    await act(async () => { run.click(); run.click(); });
    await waitFor(() => expect(request.mock.calls.filter(c => c[1].endsWith("/run"))).toHaveLength(1));
    expect(run).toBeDisabled(); expect(run).toHaveTextContent("Starting…");
    current = { ...row, lastWorkItemId: "new-run", executionStatus: "Completed" };
    fireEvent.click(screen.getByRole("button", { name: "Refresh automations" }));
    await waitFor(() => expect(run).toBeEnabled());
  });
  it("releases a rejected run and does not automatically replay a stale mutation", async () => {
    request.mockImplementation(async (_id, path) => { if (path.endsWith("/run")) throw new Error("Schedule revision is stale."); return { items: [row] }; });
    render(view()); fireEvent.click(await screen.findByText(row.name));
    fireEvent.click(screen.getByRole("button", { name: "Run automation now" }));
    expect(await screen.findByText("Schedule revision is stale.")).toBeVisible();
    expect(screen.getByRole("button", { name: "Run automation now" })).toBeEnabled();
    expect(request.mock.calls.filter(c => c[1].endsWith("/run"))).toHaveLength(1);
  });
  it("edits and disables through the same revisioned registration", async () => {
    request.mockResolvedValue({ items: [row] }); render(view());
    fireEvent.click(await screen.findByText(row.name)); fireEvent.click(screen.getByRole("button", { name: "Edit automation" }));
    fireEvent.change(screen.getByLabelText("Automation instructions"), { target: { value: "Future orders" } });
    fireEvent.click(screen.getByRole("switch", { name: "Enable automation" }));
    fireEvent.click(screen.getByRole("button", { name: "Save automation" }));
    await waitFor(() => expect(request).toHaveBeenCalledWith("instance", "automations/scheduled", "PUT", {
      expectedRevision: 2, enabled: false, name: row.name, instructions: "Future orders", trigger: row.trigger, modelKey: null, reasoningEffort: null
    }));
  });
  it("opens the exact source beyond pagination without discarding an editor draft", async () => {
    Object.defineProperty(HTMLElement.prototype, "scrollIntoView", { configurable: true, value: vi.fn() });
    const rows = Array.from({ length: 12 }, (_, index) => ({ ...row, automationId: `schedule-${index}`, name: `Task ${index}`, instructions: `Task ${index}`,
      lastWorkItemId: index === 11 ? "run-11" : null }));
    request.mockResolvedValue({ items: rows });
    const onWork = vi.fn();
    const ui = (selection?: { kind: "automation"; automationId: string; request: number }) => <ConfigProvider><App>
      <InstanceAutomationsSection instanceId="instance" onWork={onWork} selection={selection} /></App></ConfigProvider>;
    const view = render(ui());
    const section = within(screen.getByRole("region", { name: "Automations", hidden: true }));
    // Exact accessible labels avoid repeatedly computing names/styles for every
    // AntD table button. The selected source still has an explicit visibility check.
    await section.findByLabelText("View automation: Task 0");
    expect(section.queryByLabelText("View automation: Task 11")).not.toBeInTheDocument();
    // Filter before opening the editor, retaining the same draft/source scenario
    // without rerendering ten unrelated table rows for each form keystroke.
    fireEvent.change(section.getByLabelText("Search automations"), { target: { value: "Task 0" } });
    fireEvent.click(section.getByText("New automation", { exact: true }).closest("button")!);
    fireEvent.change(section.getByLabelText("Automation instructions"), { target: { value: "Unsaved task" } });
    view.rerender(ui({ kind: "automation", automationId: "schedule-11", request: 1 }));
    const source = await section.findByLabelText("View automation: Task 11");
    await waitFor(() => expect(source).toHaveAttribute("aria-expanded", "true"));
    expect(source).toBeVisible();
    expect(section.getByLabelText("Automation instructions")).toHaveValue("Unsaved task");
    expect(section.getByLabelText("Search automations")).toHaveValue("");
    const details = within(section.getByRole("region", { name: "Automation details", hidden: true }));
    expect(details.getByText("Originally created from")).toBeVisible();
    fireEvent.click(details.getByText("View last run", { exact: true }).closest("button")!);
    expect(onWork).toHaveBeenCalledWith("run-11");
  });

  it("pauses polling while hidden and retains the unsaved task when shown", async () => {
    const ui = (active: boolean) => <App><InstanceAutomationsSection instanceId="instance" active={active} onWork={vi.fn()} /></App>;
    const mounted = render(ui(true));
    await screen.findByText(/No automations yet/);
    fireEvent.click(screen.getByRole("button", { name: "New automation" }));
    fireEvent.change(screen.getByLabelText("Automation instructions"), { target: { value: "Retained draft" } });
    mounted.rerender(ui(false));
    const before = request.mock.calls.length;
    vi.useFakeTimers();
    try { await act(async () => { await vi.advanceTimersByTimeAsync(10000); }); }
    finally { vi.useRealTimers(); }
    expect(request.mock.calls.length).toBe(before);
    mounted.rerender(ui(true));
    await waitFor(() => expect(request.mock.calls.length).toBeGreaterThan(before));
    expect(screen.getByLabelText("Automation instructions")).toHaveValue("Retained draft");
  });

});
