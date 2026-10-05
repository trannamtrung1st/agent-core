import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { App as AntApp } from "antd";
import { describe, expect, it, vi } from "vitest";
import type { SessionTrigger } from "../../services/api";
import { ScheduleDrawer } from "./ScheduleDrawer";

const active: SessionTrigger = {
  registrationId: "019944af-00c5-7000-8000-000000000001",
  intent: "Call John about the very long quarterly planning note that should wrap inside the drawer",
  status: "active",
  scheduleKind: "oneShot",
  timeZone: "UTC",
  schedule: "Once on 2026-09-24 at 09:00",
  nextOccurrenceAt: "2026-09-24T09:00:00.000Z",
  revision: 1
};

function renderDrawer(load: (sessionId: string) => Promise<SessionTrigger[]>, cancel = vi.fn()) {
  return render(
    <AntApp>
      <ScheduleDrawer
        sessionId="session-1"
        open
        wide
        refreshKey={0}
        onClose={() => undefined}
        load={load}
        cancel={cancel}
      />
    </AntApp>
  );
}

describe("ScheduleDrawer", () => {
  it("loads a bounded next page, keeps existing rows on failure, and retries", async () => {
    const first = Array.from({ length: 21 }, (_, index) => ({ ...active, registrationId: `schedule-${index}`, intent: `Reminder ${index}`, status: "completed" }));
    let release: (rows: SessionTrigger[]) => void = () => undefined;
    const pending = new Promise<SessionTrigger[]>(resolve => { release = resolve; });
    const load = vi.fn().mockResolvedValueOnce(first).mockRejectedValueOnce(new Error("Temporary schedule failure")).mockReturnValueOnce(pending);
    renderDrawer(load);
    expect(await screen.findByText("Reminder 19")).toBeInTheDocument();
    expect(screen.queryByText("Reminder 20")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Temporary schedule failure")).toBeInTheDocument();
    expect(screen.getByText("Reminder 0")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(screen.getByText("Loading more…")).toBeInTheDocument();
    release([{ ...active, registrationId: "schedule-20", intent: "Reminder 20", status: "completed" }]);
    expect(await screen.findByText("Reminder 20")).toBeInTheDocument();
    expect(screen.getByText("You’re all caught up")).toBeInTheDocument();
    expect(load).toHaveBeenLastCalledWith("session-1", { limit: 21, before: "schedule-19" });
  });

  it("retains the opened page count when a conversation refresh arrives", async () => {
    const first = Array.from({ length: 21 }, (_, index) => ({ ...active, registrationId: `refresh-${index}`, intent: `Refresh reminder ${index}`, status: "completed" }));
    const tail = [first[20]];
    const load = vi.fn().mockResolvedValueOnce(first).mockResolvedValueOnce(tail).mockResolvedValueOnce(first).mockResolvedValueOnce(tail);
    const view = renderDrawer(load);
    await screen.findByText("Refresh reminder 19");
    fireEvent.click(screen.getByRole("button", { name: "Load more" }));
    await screen.findByText("Refresh reminder 20");
    view.rerender(<AntApp><ScheduleDrawer sessionId="session-1" open wide refreshKey={1}
      onClose={() => undefined} load={load} cancel={vi.fn()} /></AntApp>);
    await waitFor(() => expect(load).toHaveBeenCalledTimes(4));
    expect(screen.getByText("Refresh reminder 20")).toBeInTheDocument();
  });

  it("shows an empty list", async () => {
    renderDrawer(async () => []);
    expect(await screen.findByText("No schedules yet")).toBeInTheDocument();
    expect(screen.getByText("Upcoming and past reminders")).toBeInTheDocument();
  });

  it("shows intent, schedule, timezone, and status", async () => {
    renderDrawer(async () => [active]);
    expect(await screen.findByText(active.intent)).toBeInTheDocument();
    expect(screen.getByText(active.schedule)).toBeInTheDocument();
    expect(screen.getByText("UTC")).toBeInTheDocument();
    expect(screen.getByText("Active")).toBeInTheDocument();
    expect(screen.getByText(/Next run/)).toBeInTheDocument();
    expect(screen.queryByText(active.nextOccurrenceAt!)).not.toBeInTheDocument();
  });

  it("shows a load error", async () => {
    renderDrawer(async () => {
      throw new Error("Scheduling is disabled for this agent.");
    });
    expect(await screen.findByText("Scheduling is disabled for this agent.")).toBeInTheDocument();
  });

  it("confirms cancel and keeps the updated row", async () => {
    const cancel = vi.fn().mockResolvedValue({ ...active, status: "cancelled", revision: 2 });
    renderDrawer(async () => [active], cancel);
    fireEvent.click(await screen.findByRole("button", { name: `Cancel ${active.intent}` }));
    fireEvent.click(await screen.findByRole("button", { name: "Cancel schedule" }));
    await waitFor(() => expect(cancel).toHaveBeenCalledWith("session-1", active.registrationId, 1));
    expect(await screen.findByText("Cancelled")).toBeInTheDocument();
  });
});
