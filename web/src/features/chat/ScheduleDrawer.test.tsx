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
  it("shows an empty list", async () => {
    renderDrawer(async () => []);
    expect(await screen.findByText("No schedules")).toBeInTheDocument();
  });

  it("shows intent, schedule, timezone, and status", async () => {
    renderDrawer(async () => [active]);
    expect(await screen.findByText(active.intent)).toBeInTheDocument();
    expect(screen.getByText(active.schedule)).toBeInTheDocument();
    expect(screen.getByText("UTC")).toBeInTheDocument();
    expect(screen.getByText("Active")).toBeInTheDocument();
    expect(screen.getByText("Next")).toBeInTheDocument();
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
