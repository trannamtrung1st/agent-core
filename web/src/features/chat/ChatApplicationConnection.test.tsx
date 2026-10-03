import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { App as AntApp } from "antd";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ChatApplicationConnection } from "./ChatApplicationConnection";

vi.mock("../../services/api", () => ({
  getSession: vi.fn()
}));

vi.mock("../../services/adminApi", () => ({
  getApplicationConnection: vi.fn()
}));

vi.mock("../../services/realtime", () => ({
  suspendLiveSessionForNavigation: vi.fn(async () => undefined)
}));

import { getSession } from "../../services/api";
import { getApplicationConnection } from "../../services/adminApi";
import { suspendLiveSessionForNavigation } from "../../services/realtime";

const sessionId = "019944af-00d1-7000-8000-000000000010";
const instanceId = "019944af-00d1-7000-8000-000000000099";

function connection(status: string, displayName = "Example CRM") {
  return {
    connectionId: "connection-1",
    agentInstanceId: instanceId,
    kind: "crm",
    displayName,
    baseUrl: "https://crm.example",
    status,
    revision: 2,
    createdAtUtc: "2026-10-02T00:00:00Z",
    updatedAtUtc: "2026-10-02T00:00:00Z"
  };
}

function renderConnection() {
  return render(
    <AntApp>
      <ChatApplicationConnection sessionId={sessionId} />
    </AntApp>
  );
}

describe("ChatApplicationConnection", () => {
  beforeEach(() => {
    vi.mocked(getSession).mockReset();
    vi.mocked(getApplicationConnection).mockReset();
    vi.mocked(suspendLiveSessionForNavigation).mockClear();
    window.history.replaceState(null, "", "/c/" + sessionId);
  });

  it("renders nothing when the instance has no connection", async () => {
    vi.mocked(getSession).mockResolvedValue({ agentInstanceId: instanceId } as never);
    vi.mocked(getApplicationConnection).mockResolvedValue(null);
    renderConnection();
    await waitFor(() => expect(getApplicationConnection).toHaveBeenCalledWith(instanceId));
    expect(screen.queryByLabelText(/Application connection/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/Store/i)).not.toBeInTheDocument();
  });

  it("renders nothing when the session has no agent instance", async () => {
    vi.mocked(getSession).mockResolvedValue({ agentInstanceId: null } as never);
    renderConnection();
    await waitFor(() => expect(getSession).toHaveBeenCalledWith(sessionId));
    expect(getApplicationConnection).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: "Manage application connection" })).not.toBeInTheDocument();
  });

  it.each([
    ["Connecting", "Connecting"],
    ["Connected", "Connected"],
    ["NeedsReauthentication", "Needs reauthentication"],
    ["Unavailable", "Unavailable"],
    ["NotConnected", "Not connected"]
  ])("projects %s as Example CRM", async (status, label) => {
    vi.mocked(getSession).mockResolvedValue({ agentInstanceId: instanceId } as never);
    vi.mocked(getApplicationConnection).mockResolvedValue(connection(status));
    renderConnection();
    expect(await screen.findByLabelText(`Application connection: Example CRM, ${label}`)).toHaveTextContent(
      `Example CRM · ${label}`
    );
    expect(screen.queryByText(/Store/i)).not.toBeInTheDocument();
  });

  it("keeps a long display name in the secondary row", async () => {
    const displayName = "Northwind Regional Customer Relationship Workspace";
    vi.mocked(getSession).mockResolvedValue({ agentInstanceId: instanceId } as never);
    vi.mocked(getApplicationConnection).mockResolvedValue(connection("Connected", displayName));
    renderConnection();
    expect(await screen.findByLabelText(`Application connection: ${displayName}, Connected`)).toHaveTextContent(
      `${displayName} · Connected`
    );
  });

  it("opens the same agent instance in Admin", async () => {
    vi.mocked(getSession).mockResolvedValue({ agentInstanceId: instanceId } as never);
    vi.mocked(getApplicationConnection).mockResolvedValue(connection("Connected"));
    renderConnection();
    fireEvent.click(await screen.findByRole("button", { name: "Manage application connection" }));
    await waitFor(() => {
      expect(suspendLiveSessionForNavigation).toHaveBeenCalled();
      expect(window.location.pathname).toBe(`/admin/instances/${instanceId}`);
    });
  });
});
