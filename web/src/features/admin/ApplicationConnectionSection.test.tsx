import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { App as AntApp } from "antd";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApplicationConnectionSection } from "./ApplicationConnectionSection";

vi.mock("../../services/adminApi", () => ({
  getApplicationConnection: vi.fn(),
  connectApplication: vi.fn(),
  reauthenticateApplication: vi.fn(),
  openApplicationBrowser: vi.fn(),
  revokeApplication: vi.fn(),
  resetApplicationProfile: vi.fn()
}));

import {
  connectApplication,
  getApplicationConnection,
  openApplicationBrowser,
  revokeApplication
} from "../../services/adminApi";

const instanceId = "019944af-00d1-7000-8000-000000000001";

function renderSection() {
  return render(
    <AntApp>
      <ApplicationConnectionSection instanceId={instanceId} />
    </AntApp>
  );
}

describe("ApplicationConnectionSection", () => {
  beforeEach(() => {
    vi.mocked(getApplicationConnection).mockReset();
    vi.mocked(connectApplication).mockReset();
    vi.mocked(openApplicationBrowser).mockReset();
    vi.mocked(revokeApplication).mockReset();
  });

  it("shows not connected and connects from the form", async () => {
    vi.mocked(getApplicationConnection).mockResolvedValue(null);
    vi.mocked(connectApplication).mockResolvedValue({
      connectionId: "connection-1",
      agentInstanceId: instanceId,
      kind: "crm",
      displayName: "Example CRM",
      baseUrl: "https://crm.example",
      status: "Connecting",
      revision: 1,
      createdAtUtc: "2026-10-02T00:00:00Z",
      updatedAtUtc: "2026-10-02T00:00:00Z",
      statusDetail: "sign_in_required"
    });
    renderSection();
    expect(await screen.findByRole("heading", { name: "Application connection" })).toBeInTheDocument();
    expect(screen.getByLabelText("Display name")).toHaveValue("");
    expect(screen.getByLabelText("Base URL")).toHaveValue("");
    expect(
      screen.getByText("Connect the supported nopCommerce application to this agent. Authentication stays in this agent's browser profile.")
    ).toBeInTheDocument();
    expect(screen.getByText("Application type")).toBeInTheDocument();
    expect(screen.getByText("nopCommerce")).toBeInTheDocument();
    expect(await screen.findByText("Not connected")).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Display name"), { target: { value: "Example CRM" } });
    fireEvent.change(screen.getByLabelText("Base URL"), { target: { value: "https://crm.example" } });
    fireEvent.click(screen.getByRole("button", { name: "Connect" }));
    expect(await screen.findByText("Connecting / sign-in required")).toBeInTheDocument();
    expect(screen.getByText("Sign in is required in the application browser.")).toBeInTheDocument();
    expect(screen.getByText("Example CRM")).toBeInTheDocument();
    expect(screen.getByText("https://crm.example")).toBeInTheDocument();
    expect(connectApplication).toHaveBeenCalledWith(instanceId, "Example CRM", "https://crm.example");
    expect(screen.queryByText(/cookie|token|profile path/i)).not.toBeInTheDocument();
  });

  it("revokes a connected store without showing secrets", async () => {
    vi.mocked(getApplicationConnection).mockResolvedValue({
      connectionId: "connection-1",
      agentInstanceId: instanceId,
      kind: "crm",
      displayName: "Example CRM",
      baseUrl: "https://crm.example",
      status: "Connected",
      revision: 2,
      createdAtUtc: "2026-10-02T00:00:00Z",
      updatedAtUtc: "2026-10-02T00:00:00Z"
    });
    vi.mocked(revokeApplication).mockResolvedValue({
      connectionId: "connection-1",
      agentInstanceId: instanceId,
      kind: "crm",
      displayName: "Example CRM",
      baseUrl: "https://crm.example",
      status: "NotConnected",
      revision: 3,
      createdAtUtc: "2026-10-02T00:00:00Z",
      updatedAtUtc: "2026-10-02T00:00:00Z"
    });
    renderSection();
    expect(await screen.findByText("Connected")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Revoke connection" }));
    fireEvent.click(await screen.findByRole("button", { name: "Revoke" }));
    await waitFor(() => {
      expect(revokeApplication).toHaveBeenCalledWith(instanceId);
    });
    expect(await screen.findByText("Not connected")).toBeInTheDocument();
  });
});
