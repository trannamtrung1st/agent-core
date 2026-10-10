import { App, ConfigProvider } from "antd";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { InstanceAutomationsSection } from "./InstanceAutomationsSection";
import { instanceContinuityRequest, listEventCatalog, type Automation } from "../../services/adminApi";
import { listModels } from "../../services/api";
vi.mock("../../services/adminApi", () => ({ instanceContinuityRequest: vi.fn(), listEventCatalog: vi.fn(async () => []), eventSourceKey: (s: { kind: string; key?: string; eventId?: string }) => `${s.kind}:${s.key ?? s.eventId}` }));
vi.mock("../../services/api", () => ({ listModels: vi.fn() }));
const request = vi.mocked(instanceContinuityRequest);
const allowedPolicy = { allowOneShot: true, allowDaily: true, allowWeekly: true, allowFixedInterval: true, allowEvents: true,
  allowIndefiniteRecurrence: true, oneShotHorizonDays: 365, minRecurrenceDays: 1, minFixedIntervalSeconds: 60, maxActiveRegistrations: 32 };
const row: Automation = { executionTarget: { kind: "backgroundSession" }, completionDelivery: { kind: "none" }, automationId: "scheduled", revision: 2, name: "Review store orders", outcome: null, instructions: "Review store orders", enabled: true, status: "Active",
  triggers: [{ triggerId: "child", revision: 1, enabled: true,  kind: "schedule", schedule: { kind: "daily", interval: 1, localTime: "09:00", timeZone: "UTC" } }], authorizationOrigin: "CurrentUserTurn",
  sourceSessionId: "source-session", sourceEventId: null, createdAt: "2026-10-05T00:00:00Z", nextRunAt: "2026-10-06T09:00:00Z",
  modelKey: null, reasoningEffort: null, effectiveModelKey: "scripted-alpha", lastAgentRunId: null, executionStatus: null };
const view = () => <ConfigProvider theme={{ token: { motion: false } }}><App><InstanceAutomationsSection instanceId="instance" onWork={vi.fn()} /></App></ConfigProvider>;
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
beforeEach(() => {
  vi.clearAllMocks(); vi.mocked(listModels).mockResolvedValue({ defaultKey: "scripted-alpha", models: [] });
  vi.mocked(listEventCatalog).mockResolvedValue([]);
  request.mockResolvedValue({ items: [], policy: allowedPolicy });
});
describe("Owner schedule authoring", () => {
  it.each([false, true])("validates an unavailable Webhook sibling only when enabled=%s", async webhookEnabled => {
    vi.mocked(listEventCatalog).mockImplementation(async kind => {
      if (kind === "webhook") throw new Error("Webhook unavailable");
      return [{ source: { kind: "builtin", key: "run.completed" }, key: "run.completed", name: "Run completed",
        description: "Completed Run", state: "Active", schemaVersion: 1, example: {}, fieldSchema: {} }];
    });
    const triggers: Automation["triggers"] = [
      { triggerId: "healthy", revision: 1, enabled: true, kind: "event", source: { kind: "builtin", key: "run.completed" } },
      { triggerId: "unavailable", revision: 2, enabled: webhookEnabled, kind: "event", source: { kind: "webhook", eventId: "retained-webhook" } },
    ];
    request.mockResolvedValue({ items: [{ ...row, triggers }], policy: { ...allowedPolicy, allowCoreEvents: true } });
    render(view());
    fireEvent.click(await screen.findByRole("button", { name: `View automation: ${row.name}` }));
    await screen.findByRole("button", { name: "Reload Events and models" });
    fireEvent.click(screen.getByRole("button", { name: "Edit automation" }));
    const save = screen.getByRole("button", { name: "Save automation" });
    if (webhookEnabled) {
      expect(save).toBeDisabled();
      expect(screen.getByRole("button", { name: "Save as disabled" })).toBeEnabled();
    } else {
      expect(save).toBeEnabled();
      fireEvent.click(save);
      await waitFor(() => expect(request).toHaveBeenCalledWith("instance", "automations/scheduled", "PUT",
        expect.objectContaining({ enabled: true, triggers })));
    }
  });
  it("saves an enabled Schedule despite an unrelated Webhook catalog failure", async () => {
    vi.mocked(listEventCatalog).mockImplementation(async kind => { if (kind === "webhook") throw new Error("Webhook unavailable"); return []; });
    request.mockResolvedValue({ items: [row], policy: allowedPolicy });
    render(view());
    fireEvent.click(await screen.findByRole("button", { name: `View automation: ${row.name}` }));
    await screen.findByRole("button", { name: "Reload Events and models" });
    fireEvent.click(screen.getByRole("button", { name: "Edit automation" }));
    expect(screen.getByRole("button", { name: "Save automation" })).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "Save automation" }));
    await waitFor(() => expect(request).toHaveBeenCalledWith("instance", "automations/scheduled", "PUT", expect.objectContaining({ enabled: true })));
  });
  it("keeps a known Event draft saveable as disabled after catalog and collection refresh failures", async () => {
    vi.mocked(listEventCatalog).mockRejectedValue(new Error("Catalog unavailable"));
    request.mockResolvedValue({ items: [{ ...row, triggers: [{ triggerId: "known", revision: 1, enabled: true, kind: "event", source: { kind: "webhook", eventId: "known-event" } }] }], policy: allowedPolicy });
    render(view());
    fireEvent.click(await screen.findByRole("button", { name: `View automation: ${row.name}` }));
    fireEvent.click(screen.getByRole("button", { name: "Edit automation" }));
    request.mockImplementation(async (_instance, _path, method) => { if (!method) throw new Error("Collection refresh failed"); return row; });
    fireEvent.click(screen.getByRole("button", { name: "Refresh automations" }));
    await screen.findByText(/Collection refresh failed/);
    expect(screen.getByRole("button", { name: "Save automation" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Save as disabled" }));
    await waitFor(() => expect(request).toHaveBeenCalledWith("instance", "automations/scheduled", "PUT", expect.objectContaining({ enabled: false, triggers: [expect.objectContaining({ source: { kind: "webhook", eventId: "known-event" } })] })));
  });
  it("updates the save summary for schedule, background reporting and disabled state", async () => {
    render(view()); await screen.findByText(/No automations yet/);
    fireEvent.click(screen.getByRole("button", { name: "New automation" }));
    const summary = within(screen.getByRole("region", { name: "Automation summary" }));
    expect(summary.getByText(/Every 1 day/)).toBeVisible();
    expect(summary.getByText("Run this agent in a background Session")).toBeVisible();
    expect(summary.getByText("Keep results in Background work without a conversation report")).toBeVisible();
    fireEvent.change(screen.getByLabelText("Schedule local time"), { target: { value: "14:30" } });
    expect(summary.getByText(/14:30/)).toBeVisible();
    expect(screen.getByRole("switch", { name: "Enable automation" })).not.toBeChecked();
    expect(summary.getByText("Saved disabled. Enable this Automation before it can run.")).toBeVisible();
    fireEvent.mouseDown(screen.getByLabelText("Automation completion report"));
    fireEvent.click(await screen.findByText("Selected conversation", { selector: ".ant-select-item-option-content" }));
    expect(summary.getByText("Choose a conversation for the completion report")).toBeVisible();
    expect(screen.getByRole("button", { name: "Create automation" })).toBeDisabled();
  });
  it("allows disabled Schedule authoring when the Definition has no policy", async () => {
    request.mockResolvedValue({ items: [], policy: null });
    render(view()); await screen.findByText(/No automations yet/);
    fireEvent.click(screen.getByRole("button", { name: "New automation" }));
    fireEvent.change(screen.getByLabelText("Automation name"), { target: { value: "Blocked schedule" } });
    fireEvent.change(screen.getByLabelText("Automation instructions"), { target: { value: "Do not admit this" } });
    expect(screen.getByText("This Definition does not permit Schedule Automations.")).toBeVisible();
    expect(screen.getByRole("button", { name: "Create automation" })).toBeEnabled();
  });
  it("keeps Event load failures visible after an Automation refresh and retries the catalog", async () => {
    vi.mocked(listEventCatalog).mockRejectedValueOnce(new Error("Event catalog unavailable"));
    render(view());
    const retry = await screen.findByRole("button", { name: "Reload Events and models" });
    await screen.findByText(/No automations yet/);
    fireEvent.click(retry);
    await waitFor(() => expect(screen.queryByRole("button", { name: "Reload Events and models" })).not.toBeInTheDocument());
    expect(listEventCatalog).toHaveBeenCalledTimes(4);
  });
  it("explains a missing Event policy before saving an existing subscription", async () => {
    request.mockResolvedValue({ items: [{ ...row, triggers: [{ triggerId: "child", revision: 1, enabled: true,  kind: "event", source: { kind: "webhook", eventId: "shared-event" } }] }], policy: null });
    render(view());
    fireEvent.click(await screen.findByRole("button", { name: `View automation: ${row.name}` }));
    fireEvent.click(screen.getByRole("button", { name: "Edit automation" }));
    expect(await screen.findByText("Allow Webhook Events in the Definition and activate this Event to run this subscription.")).toBeVisible();
    expect(screen.getByRole("button", { name: "Save automation" })).toBeDisabled();
  });
  it.each(["Completed", "Expired"])("deletes %s automations with the current revision", async status => {
    const completed = { ...row, status, enabled: false, lastAgentRunId: "retained-run", executionStatus: "Completed" };
    let removed = false;
    request.mockImplementation(async (_instance, _path, method) => {
      if (method === "DELETE") { removed = true; return { cancelled: true }; }
      return { items: removed ? [] : [completed] };
    });
    render(view());
    fireEvent.click(await screen.findByRole("button", { name: `View automation: ${row.name}` }));
    const details = within(screen.getByRole("region", { name: "Automation details" }));
    expect(details.getByRole("button", { name: "Run automation now" })).toBeDisabled();
    expect(details.getByRole("button", { name: "View last run" })).toBeEnabled();
    fireEvent.click(details.getByRole("button", { name: "Delete automation" }));
    const confirmation = await screen.findByRole("dialog");
    fireEvent.click(within(confirmation).getByRole("button", { name: "Delete automation" }));
    await waitFor(() => expect(request).toHaveBeenCalledWith("instance", "automations/scheduled", "DELETE", { expectedRevision: 2 }));
    await screen.findByText(/No automations yet/);
    await waitFor(() => expect(screen.getByRole("button", { name: "New automation" })).toHaveFocus());
  });
  it.each([["WaitingForApproval", "Needs approval"], ["WaitingToRetry", "Retrying"]])("uses readable run state for %s", async (executionStatus, label) => {
    request.mockResolvedValue({ items: [{ ...row, executionStatus, lastAgentRunId: "last-run" }] });
    render(view());
    await screen.findByRole("button", { name: `View automation: ${row.name}` });
    fireEvent.click(screen.getByRole("button", { name: `View automation: ${row.name}` }));
    expect(screen.getByRole("region", { name: "Automation details" })).toHaveTextContent(label);
    expect(screen.getByRole("button", { name: "Run automation now" })).toBeDisabled();
  });
  it("refreshes a source created after the cached review when returning from Runs", async () => {
    Object.defineProperty(HTMLElement.prototype, "scrollIntoView", { configurable: true, value: vi.fn() });
    request.mockResolvedValue({ items: [] });
    const ui = (selection?: { kind: "automation"; automationId: string; request: number }) => <ConfigProvider theme={{ token: { motion: false } }}><App>
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
    expect(within(section).getByRole("region", { name: "Automation details" })).toHaveTextContent("Conversation unique-c");
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
    fireEvent.mouseDown(screen.getByLabelText("Schedule time zone"));
    fireEvent.change(screen.getByLabelText("Schedule time zone"), { target: { value: "Asia/Tokyo" } });
    fireEvent.click((await screen.findAllByText("Asia/Tokyo")).find(e => e.classList.contains("ant-select-item-option-content"))!);
    fireEvent.click(screen.getByRole("button", { name: "Create automation" }));
    await waitFor(() => expect(request).toHaveBeenCalledWith("instance", "automations", "POST", {
      executionTarget: { kind: "backgroundSession" }, completionDelivery: { kind: "none" }, expectedRevision: 0, enabled: false, name: "Review orders", instructions: "Review pending orders", modelKey: null, reasoningEffort: null,
      triggers: [{ triggerId: expect.any(String), revision: 1, enabled: true,  kind: "schedule", schedule: { kind: "daily", timeZone: "Asia/Tokyo", interval: 1, localTime: "09:00" } }]
    }));
  });
  it("requires a finite bound under Definition policy and sends the selected occurrence limit", async () => {
    request.mockResolvedValue({ items: [], policy: { allowOneShot: true, allowDaily: true, allowWeekly: true, allowFixedInterval: true,
      allowIndefiniteRecurrence: false, oneShotHorizonDays: 10, minRecurrenceDays: 1, minFixedIntervalSeconds: 300, maxActiveRegistrations: 2 } });
    render(view()); await screen.findByText(/No automations yet/);
    fireEvent.click(screen.getByRole("button", { name: "New automation" }));
    fireEvent.change(screen.getByLabelText("Automation name"), { target: { value: "Audit" } });
    fireEvent.change(screen.getByLabelText("Automation instructions"), { target: { value: "Finite store audit" } });
    fireEvent.click(screen.getByRole("switch", { name: "Enable automation" }));
    expect(screen.getByText(/This Definition requires an end date/)).toBeVisible();
    expect(screen.getByRole("button", { name: "Create automation" })).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Schedule maximum occurrences"), { target: { value: "367" } });
    expect(screen.getByRole("button", { name: "Create automation" })).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Schedule maximum occurrences"), { target: { value: "3" } });
    fireEvent.click(screen.getByRole("button", { name: "Create automation" }));
    await waitFor(() => expect(request).toHaveBeenCalledWith("instance", "automations", "POST", expect.objectContaining({
      triggers: [{ triggerId: expect.any(String), revision: 1, enabled: true,  kind: "schedule", schedule: expect.objectContaining({ maxOccurrences: 3 }) }]
    })));
  });
  it("preserves chat provenance and keeps accepted run locked across stale status until a new execution", async () => {
    let current = row; request.mockImplementation(async () => ({ items: [current] }));
    render(view()); fireEvent.click(await screen.findByText(row.name));
    expect(screen.getByRole("link", { name: /Conversation source-s/ })).toBeVisible();
    expect(screen.getByText("Originally created from")).toBeVisible();
    const run = screen.getByRole("button", { name: "Run automation now" });
    await act(async () => { run.click(); run.click(); });
    await waitFor(() => expect(request.mock.calls.filter(c => c[1].endsWith("/run"))).toHaveLength(1));
    expect(run).toBeDisabled(); expect(run).toHaveTextContent("Starting…");
    current = { ...row, lastAgentRunId: "new-run", executionStatus: "Completed" };
    fireEvent.click(screen.getByRole("button", { name: "Refresh automations" }));
    await waitFor(() => expect(run).toBeEnabled());
  });
  it("releases a rejected run and does not automatically replay a stale mutation", async () => {
    request.mockImplementation(async (_id, path) => { if (path.endsWith("/run")) throw new Error("Schedule revision is stale."); return { items: [row] }; });
    render(view()); fireEvent.click(await screen.findByText(row.name));
    fireEvent.click(screen.getByRole("button", { name: "Run automation now" }));
    await waitFor(() => expect(screen.getByText("Schedule revision is stale.")).toBeVisible());
    expect(screen.getByRole("button", { name: "Run automation now" })).toBeEnabled();
    expect(request.mock.calls.filter(c => c[1].endsWith("/run"))).toHaveLength(1);
  });
  it("edits and disables through the same revisioned registration", async () => {
    request.mockResolvedValue({ items: [row], policy: allowedPolicy }); render(view());
    fireEvent.click(await screen.findByText(row.name)); fireEvent.click(screen.getByRole("button", { name: "Edit automation" }));
    fireEvent.change(screen.getByLabelText("Automation instructions"), { target: { value: "Future orders" } });
    fireEvent.click(screen.getByRole("switch", { name: "Enable automation" }));
    fireEvent.click(screen.getByRole("button", { name: "Save automation" }));
    await waitFor(() => expect(request).toHaveBeenCalledWith("instance", "automations/scheduled", "PUT", {
      executionTarget: row.executionTarget, completionDelivery: row.completionDelivery, requiresTools: undefined, requiresVision: undefined, expectedRevision: 2, enabled: false, name: row.name, instructions: "Future orders", triggers: row.triggers, modelKey: null, reasoningEffort: null
    }));
  });
  it("opens the exact source beyond pagination without discarding an editor draft", async () => {
    Object.defineProperty(HTMLElement.prototype, "scrollIntoView", { configurable: true, value: vi.fn() });
    const rows = Array.from({ length: 12 }, (_, index) => ({ ...row, automationId: `schedule-${index}`, name: `Task ${index}`, instructions: `Task ${index}`,
      lastAgentRunId: index === 11 ? "run-11" : null }));
    request.mockResolvedValue({ items: rows });
    const onWork = vi.fn();
    const ui = (selection?: { kind: "automation"; automationId: string; request: number }) => <ConfigProvider theme={{ token: { motion: false } }}><App>
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

  it("retains a hidden editor draft when another automation is disabled", async () => {
    request.mockResolvedValue({ items: [row] });
    const ui = (active: boolean) => <ConfigProvider theme={{ token: { motion: false } }}><App>
      <InstanceAutomationsSection instanceId="instance" active={active} onWork={vi.fn()} /></App></ConfigProvider>;
    const mounted = render(ui(true));
    fireEvent.click(await screen.findByRole("button", { name: `View automation: ${row.name}` }));
    fireEvent.click(screen.getByRole("button", { name: "New automation" }));
    fireEvent.change(screen.getByLabelText("Automation instructions"), { target: { value: "Keep this draft" } });
    mounted.rerender(ui(false));
    fireEvent.click(screen.getByRole("button", { name: "Disable automation" }));
    await waitFor(() => expect(request).toHaveBeenCalledWith("instance", "automations/scheduled", "PUT", expect.objectContaining({ enabled: false })));
    await waitFor(() => expect(screen.getByRole("button", { name: "Disable automation" })).toBeEnabled());
    await act(async () => { mounted.rerender(ui(true)); });
    expect(screen.getByLabelText("Automation instructions")).toHaveValue("Keep this draft");
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
