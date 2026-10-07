import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App as AntApp, ConfigProvider } from "antd";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { EventSourcesSection, webhookUrl } from "./EventSourcesSection";

vi.mock("../../services/adminApi", () => ({
  listEventSources: vi.fn(),
  createEventSource: vi.fn(),
  rotateEventSource: vi.fn(),
  revokeEventSource: vi.fn()
}));

import {
  createEventSource,
  listEventSources,
  revokeEventSource,
  rotateEventSource
} from "../../services/adminApi";

const sourceKey = "11111111-1111-1111-1111-111111111111";
const sourceId = "22222222-2222-2222-2222-222222222222";

function renderSection() {
  return render(
    <ConfigProvider theme={{ token: { motion: false } }}><AntApp>
      <EventSourcesSection />
    </AntApp></ConfigProvider>
  );
}

describe("EventSourcesSection", () => {
  beforeEach(() => {
    vi.mocked(listEventSources).mockReset();
    vi.mocked(createEventSource).mockReset();
    vi.mocked(rotateEventSource).mockReset();
    vi.mocked(revokeEventSource).mockReset();
  });

  it("shows an empty state, then a credential only once", async () => {
    vi.mocked(listEventSources).mockResolvedValue([]);
    vi.mocked(createEventSource).mockResolvedValue({
      sourceId,
      sourceKey,
      token: "secret-credential-value",
      status: "Active"
    });
    vi.mocked(rotateEventSource).mockResolvedValue({
      sourceId,
      sourceKey,
      token: "rotated-credential-value",
      status: "Active"
    });
    vi.mocked(revokeEventSource).mockResolvedValue({
      sourceId,
      displayName: "Demo Store",
      kind: "Webhook",
      sourceKey,
      status: "Revoked",
      revision: 3
    });
    renderSection();
    expect(await screen.findByText("No event sources yet.")).toBeInTheDocument();
    expect(screen.getByText(/does not grant an agent/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "New event source" }));
    fireEvent.change(screen.getByLabelText("Event source name"), { target: { value: "Demo Store" } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Create event source" })); });
    await waitFor(() => expect(createEventSource).toHaveBeenCalledWith("Demo Store"));
    const credential = (await screen.findByLabelText("Event source credential")).closest('[role="dialog"]') as HTMLElement;
    expect(within(credential).getByText("Copy this credential")).toBeInTheDocument();
    expect(within(credential).getByLabelText("Event source credential")).toHaveValue("secret-credential-value");
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(screen.queryByLabelText("Event source credential")).not.toBeInTheDocument());
    expect(screen.queryByText("secret-credential-value")).not.toBeInTheDocument();
    expect(screen.getByLabelText("Source key")).toHaveTextContent(sourceKey);

    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Copy webhook URL for Demo Store" })); });
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(webhookUrl(sourceKey)));
    expect(webhookUrl(sourceKey)).toBe(`${window.location.origin}/api/v1/hooks/${sourceKey}`);

    fireEvent.click(screen.getByRole("button", { name: "Rotate credential for Demo Store" }));
    const rotate = await screen.findByRole("dialog");
    fireEvent.click(within(rotate).getByRole("button", { name: "Keep" }));
    expect(rotateEventSource).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: "Rotate credential for Demo Store" }));
    const rotateConfirmation = await screen.findByRole("dialog");
    await act(async () => { fireEvent.click(within(rotateConfirmation).getByRole("button", { name: "Rotate credential" })); });
    await waitFor(() => expect(rotateEventSource).toHaveBeenCalledWith(sourceId));
    expect(await screen.findByLabelText("Event source credential")).toHaveValue("rotated-credential-value");
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(screen.queryByText("rotated-credential-value")).not.toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: "Revoke Demo Store" }));
    const revoke = await screen.findByRole("dialog");
    await act(async () => { fireEvent.click(within(revoke).getByRole("button", { name: "Revoke source" })); });
    await waitFor(() => expect(revokeEventSource).toHaveBeenCalledWith(sourceId));
    expect(await screen.findByText("Revoked")).toBeInTheDocument();
    expect(screen.getByText("Webhook")).toBeInTheDocument();
    expect(screen.queryByText("secret-credential-value")).not.toBeInTheDocument();
  });
  it("does not present a failed inventory read as empty and retries", async () => {
    vi.mocked(listEventSources).mockRejectedValueOnce(new Error("Sources unavailable")).mockResolvedValueOnce([]);
    renderSection();
    expect(await screen.findByText("Sources unavailable")).toBeVisible();
    expect(screen.queryByText("No event sources yet.")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));
    expect(await screen.findByText("No event sources yet.")).toBeVisible();
    expect(screen.getByRole("button", { name: "New event source" })).toBeVisible();
  });

  it("keeps clipboard failure and manual copy inside the one-time credential dialog", async () => {
    vi.mocked(listEventSources).mockResolvedValue([]);
    vi.mocked(createEventSource).mockResolvedValue({ sourceId, sourceKey, token: "one-time-token", status: "Active" });
    Object.assign(navigator, { clipboard: { writeText: vi.fn().mockRejectedValue(new Error("Denied")) } });
    renderSection();
    fireEvent.click(await screen.findByRole("button", { name: "New event source" }));
    fireEvent.change(await screen.findByLabelText("Event source name"), { target: { value: "Store" } });
    fireEvent.click(screen.getByRole("button", { name: "Create event source" }));
    const dialog = (await screen.findByLabelText("Event source credential")).closest('[role="dialog"]') as HTMLElement;
    fireEvent.click(within(dialog).getByRole("button", { name: "Copy event source credential" }));
    await waitFor(() => expect(within(dialog).getByText("Copy failed. Select the value and copy it manually.")).toBeVisible());
    expect(within(dialog).getByLabelText("Event source credential")).toHaveValue("one-time-token");
    fireEvent.click(within(dialog).getByRole("button", { name: "Done" }));
    await waitFor(() => expect(screen.queryByLabelText("Event source credential")).not.toBeInTheDocument());
    expect(screen.queryByText(/Copy failed/)).not.toBeInTheDocument();
  });

  it("retains a failed creation draft for retry and clears cancelled drafts", async () => {
    vi.mocked(listEventSources).mockResolvedValue([]);
    vi.mocked(createEventSource).mockRejectedValueOnce(new Error("Temporary failure"))
      .mockResolvedValueOnce({ sourceId, sourceKey, token: "retry-token", status: "Active" });
    renderSection();
    fireEvent.click(await screen.findByRole("button", { name: "New event source" }));
    const drawer = await screen.findByRole("dialog", { name: "New event source" });
    expect(within(drawer).getByRole("button", { name: "Create event source" })).toBeDisabled();
    fireEvent.change(within(drawer).getByLabelText("Event source name"), { target: { value: " Retry Store " } });
    fireEvent.click(within(drawer).getByRole("button", { name: "Create event source" }));
    expect(await within(drawer).findByText("Temporary failure")).toBeVisible();
    expect(within(drawer).getByLabelText("Event source name")).toHaveValue(" Retry Store ");
    fireEvent.click(within(drawer).getByRole("button", { name: "Create event source" }));
    const credential = (await screen.findByLabelText("Event source credential")).closest('[role="dialog"]') as HTMLElement;
    expect(createEventSource).toHaveBeenLastCalledWith("Retry Store");
    fireEvent.click(within(credential).getByRole("button", { name: "Done" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: "New event source" }));
    fireEvent.change(screen.getByLabelText("Event source name"), { target: { value: "Discard me" } });
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: "New event source" }));
    expect(screen.getByLabelText("Event source name")).toHaveValue("");
  });

});
