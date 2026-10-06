import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App as AntApp } from "antd";
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
    <AntApp>
      <EventSourcesSection />
    </AntApp>
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
    fireEvent.change(screen.getByLabelText("Event source name"), { target: { value: "Demo Store" } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Create event source" })); });
    await waitFor(() => expect(createEventSource).toHaveBeenCalledWith("Demo Store"));
    const credential = await screen.findByRole("dialog");
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
});
