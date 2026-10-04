import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App as AntApp } from "antd";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApplicationConnectionSection } from "./ApplicationConnectionSection";

vi.mock("../../services/adminApi", () => ({
  getApplicationConnection: vi.fn(),
  connectApplication: vi.fn(),
  reauthenticateApplication: vi.fn(),
  openApplicationBrowser: vi.fn(),
  revokeApplication: vi.fn(),
  resetApplicationProfile: vi.fn(),
  issueApplicationWebhook: vi.fn(),
  revokeApplicationWebhook: vi.fn()
}));

import {
  connectApplication,
  getApplicationConnection,
  issueApplicationWebhook,
  openApplicationBrowser,
  revokeApplication,
  revokeApplicationWebhook
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
      kind: "nopCommerce",
      displayName: "Demo Store",
      baseUrl: "https://store.example",
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
    fireEvent.change(screen.getByLabelText("Display name"), { target: { value: "Demo Store" } });
    fireEvent.change(screen.getByLabelText("Base URL"), { target: { value: "https://store.example" } });
    fireEvent.click(screen.getByRole("button", { name: "Connect" }));
    expect(await screen.findByText("Connecting / sign-in required")).toBeInTheDocument();
    expect(screen.getByText("Sign in is required in the application browser.")).toBeInTheDocument();
    expect(screen.getByText("Demo Store")).toBeInTheDocument();
    expect(screen.getByText("https://store.example")).toBeInTheDocument();
    expect(connectApplication).toHaveBeenCalledWith(instanceId, "Demo Store", "https://store.example");
    expect(screen.queryByText(/cookie|token|profile path/i)).not.toBeInTheDocument();
  });

  it("revokes a connected store without showing secrets", async () => {
    vi.mocked(getApplicationConnection).mockResolvedValue({
      connectionId: "connection-1",
      agentInstanceId: instanceId,
      kind: "nopCommerce",
      displayName: "Demo Store",
      baseUrl: "https://store.example",
      status: "Connected",
      revision: 2,
      createdAtUtc: "2026-10-02T00:00:00Z",
      updatedAtUtc: "2026-10-02T00:00:00Z"
    });
    vi.mocked(revokeApplication).mockResolvedValue({
      connectionId: "connection-1",
      agentInstanceId: instanceId,
      kind: "nopCommerce",
      displayName: "Demo Store",
      baseUrl: "https://store.example",
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

  it("shows a webhook credential once and can revoke it", async () => {
    vi.mocked(getApplicationConnection).mockResolvedValue({
      connectionId: "connection-1",
      agentInstanceId: instanceId,
      kind: "nopCommerce",
      displayName: "Demo Store",
      baseUrl: "https://store.example",
      status: "Connected",
      revision: 2,
      createdAtUtc: "2026-10-02T00:00:00Z",
      updatedAtUtc: "2026-10-02T00:00:00Z",
      webhookKey: null,
      webhookStatus: "Not configured"
    });
    vi.mocked(issueApplicationWebhook).mockResolvedValue({
      webhookKey: "11111111-1111-1111-1111-111111111111",
      token: "secret-credential-value",
      status: "Active"
    });
    vi.mocked(revokeApplicationWebhook).mockResolvedValue({
      connectionId: "connection-1",
      agentInstanceId: instanceId,
      kind: "nopCommerce",
      displayName: "Demo Store",
      baseUrl: "https://store.example",
      status: "Connected",
      revision: 4,
      createdAtUtc: "2026-10-02T00:00:00Z",
      updatedAtUtc: "2026-10-02T00:00:00Z",
      webhookKey: "11111111-1111-1111-1111-111111111111",
      webhookStatus: "Revoked"
    });
    renderSection();
    expect(await screen.findByText("Not configured")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Create webhook" }));
    const credential = await screen.findByRole("dialog", { name: "Copy this credential" });
    expect(within(credential).getByLabelText("Webhook credential")).toHaveValue("secret-credential-value");
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(screen.queryByLabelText("Webhook credential")).not.toBeInTheDocument());
    expect(screen.queryByText("secret-credential-value")).not.toBeInTheDocument();
    expect(screen.getByLabelText("Webhook key")).toHaveTextContent("11111111-1111-1111-1111-111111111111");
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });
    fireEvent.click(screen.getByRole("button", { name: "Copy webhook URL" }));
    await waitFor(() => {
      expect(writeText).toHaveBeenCalledWith(
        `${window.location.origin}/api/v1/hooks/11111111-1111-1111-1111-111111111111/order-placed`
      );
    });
    fireEvent.click(screen.getByRole("button", { name: "Rotate webhook" }));
    const rotate = await screen.findByRole("dialog", { name: "Rotate this webhook?" });
    fireEvent.click(within(rotate).getByRole("button", { name: "Keep" }));
    expect(issueApplicationWebhook).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole("button", { name: "Rotate webhook" }));
    fireEvent.click(within(await screen.findByRole("dialog", { name: "Rotate this webhook?" })).getByRole("button", { name: "Rotate webhook" }));
    await waitFor(() => expect(issueApplicationWebhook).toHaveBeenCalledTimes(2));
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(screen.queryByLabelText("Webhook credential")).not.toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: "Revoke webhook" }));
    const dialog = await screen.findByRole("dialog", { name: "Revoke this webhook?" });
    fireEvent.click(within(dialog).getByRole("button", { name: "Revoke webhook" }));
    await waitFor(() => expect(revokeApplicationWebhook).toHaveBeenCalledWith(instanceId));
    expect(await screen.findByText("Revoked")).toBeInTheDocument();
    expect(screen.queryByText("secret-credential-value")).not.toBeInTheDocument();
  });
});
