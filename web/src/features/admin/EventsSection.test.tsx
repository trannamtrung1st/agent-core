import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App as AntApp, ConfigProvider } from "antd";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { EventsSection } from "./EventsSection";

vi.mock("../../services/adminApi", () => ({
  listWebhookEvents: vi.fn(),
  createWebhookEvent: vi.fn(),
  rotateWebhookEvent: vi.fn(),
  revokeWebhookEvent: vi.fn(), getWebhookEvent: vi.fn(), renameWebhookEvent: vi.fn()
}));

import {
  createWebhookEvent,
  listWebhookEvents,
  revokeWebhookEvent,
  rotateWebhookEvent, getWebhookEvent
} from "../../services/adminApi";

const eventKey = "order.placed";
const eventId = "22222222-2222-2222-2222-222222222222";

function renderSection() {
  return render(
    <ConfigProvider theme={{ token: { motion: false } }}><AntApp>
      <EventsSection />
    </AntApp></ConfigProvider>
  );
}

describe("EventsSection", () => {
  beforeEach(() => {
    vi.mocked(listWebhookEvents).mockReset();
    vi.mocked(createWebhookEvent).mockReset();
    vi.mocked(rotateWebhookEvent).mockReset();
    vi.mocked(revokeWebhookEvent).mockReset();
    vi.mocked(getWebhookEvent).mockReset();
    window.history.replaceState(null, "", "/admin/connections/events");
  });

  it("shows an empty state, then a credential only once", async () => {
    vi.mocked(listWebhookEvents).mockResolvedValue([]);
    vi.mocked(createWebhookEvent).mockResolvedValue({
      eventId,
      eventKey,
      token: "secret-credential-value",
      status: "Active"
    });
    vi.mocked(rotateWebhookEvent).mockResolvedValue({
      eventId,
      eventKey,
      token: "rotated-credential-value",
      status: "Active"
    });
    vi.mocked(revokeWebhookEvent).mockResolvedValue({
      eventId,
      displayName: "Demo Store",
      createdAt: "2026-10-08T00:00:00Z", updatedAt: "2026-10-08T00:00:00Z", subscriberCount: 2, activeSubscriberCount: 1, lastReceivedAt: null,
      eventKey,
      status: "Revoked",
      revision: 3
    });
    renderSection();
    expect(await screen.findByText("No Events yet. Create an Event, then subscribe an Automation to it.")).toBeInTheDocument();

    vi.mocked(listWebhookEvents).mockResolvedValue([{ eventId, eventKey, displayName: "Demo Store", status: "Active", revision: 1,
      createdAt: "2026-10-08T00:00:00Z", updatedAt: "2026-10-08T00:00:00Z", subscriberCount: 2, activeSubscriberCount: 1, lastReceivedAt: null }]);
    fireEvent.click(screen.getByRole("button", { name: "New Event" }));
    fireEvent.change(screen.getByLabelText("Event key"), { target: { value: eventKey } });
    fireEvent.change(screen.getByLabelText("Event name"), { target: { value: "Demo Store" } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Create Event" })); });
    await waitFor(() => expect(createWebhookEvent).toHaveBeenCalledWith("Demo Store", eventKey));
    const credential = (await screen.findByLabelText("Event credential")).closest('[role="dialog"]') as HTMLElement;
    expect(within(credential).getByText("Copy this credential")).toBeInTheDocument();
    expect(within(credential).getByLabelText("Event credential")).toHaveValue("secret-credential-value");
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(screen.queryByLabelText("Event credential")).not.toBeInTheDocument());
    expect(screen.queryByText("secret-credential-value")).not.toBeInTheDocument();
    expect(screen.getByText(eventKey)).toBeVisible();

    fireEvent.click(screen.getByRole("button", { name: "More actions for Demo Store" }));
    fireEvent.click(await screen.findByRole("menuitem", { name: "Rotate credential" }));
    const rotate = await screen.findByRole("dialog");
    fireEvent.click(within(rotate).getByRole("button", { name: "Cancel" }));
    expect(rotateWebhookEvent).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: "More actions for Demo Store" }));
    fireEvent.click(await screen.findByRole("menuitem", { name: "Rotate credential" }));
    const rotateConfirmation = await screen.findByRole("dialog");
    await act(async () => { fireEvent.click(within(rotateConfirmation).getByRole("button", { name: "Rotate credential" })); });
    await waitFor(() => expect(rotateWebhookEvent).toHaveBeenCalledWith(eventId));
    expect(await screen.findByLabelText("Event credential")).toHaveValue("rotated-credential-value");
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(screen.queryByText("rotated-credential-value")).not.toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: "More actions for Demo Store" }));
    fireEvent.click(await screen.findByRole("menuitem", { name: "Revoke Event" }));
    const revoke = await screen.findByRole("dialog");
    await act(async () => { fireEvent.click(within(revoke).getByRole("button", { name: "Revoke Event" })); });
    await waitFor(() => expect(revokeWebhookEvent).toHaveBeenCalledWith(eventId));


    expect(screen.queryByText("secret-credential-value")).not.toBeInTheDocument();
  });
  it("does not present a failed inventory read as empty and retries", async () => {
    vi.mocked(listWebhookEvents).mockRejectedValueOnce(new Error("Sources unavailable")).mockResolvedValueOnce([]);
    renderSection();
    expect(await screen.findByText("Sources unavailable")).toBeVisible();
    expect(screen.queryByText("No Events yet. Create an Event, then subscribe an Automation to it.")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));
    expect(await screen.findByText("No Events yet. Create an Event, then subscribe an Automation to it.")).toBeVisible();
    expect(screen.getByRole("button", { name: "New Event" })).toBeVisible();
  });

  it("names reactivation explicitly and cancellation leaves the Event revoked", async () => {
    vi.mocked(listWebhookEvents).mockResolvedValue([{ eventId, eventKey, displayName: "Revoked store", status: "Revoked", revision: 2,
      createdAt: "2026-10-08T00:00:00Z", updatedAt: "2026-10-08T00:00:00Z", subscriberCount: 2, activeSubscriberCount: 1, lastReceivedAt: null }]);
    renderSection();
    fireEvent.click(await screen.findByRole("button", { name: "More actions for Revoked store" }));
    expect(screen.queryByRole("menuitem", { name: "Rotate credential" })).not.toBeInTheDocument();
    await act(async () => { fireEvent.click(screen.getByRole("menuitem", { name: "Reactivate Event" })); });
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("accept new signals again");
    expect(within(dialog).getByRole("button", { name: "Reactivate Event" })).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(dialog).not.toBeInTheDocument());
    expect(rotateWebhookEvent).not.toHaveBeenCalled();
    expect(screen.getByText("Revoked", { selector: ".ant-tag" })).toBeVisible();
    expect(screen.getByRole("button", { name: "More actions for Revoked store" })).toHaveFocus();
  });

  it("shows unsubscribed signals separately, truthful counts and copyable request examples", async () => {
    const event = { eventId, eventKey, displayName: "Activity store", status: "Active", revision: 1,
      createdAt: "2026-10-08T00:00:00Z", updatedAt: "2026-10-08T00:00:00Z", subscriberCount: 2, activeSubscriberCount: 1, lastReceivedAt: "2026-10-08T01:00:00Z" };
    vi.mocked(listWebhookEvents).mockResolvedValue([event]);
    vi.mocked(getWebhookEvent).mockResolvedValue({ event, subscribers: [], deliveries: [], signals: [{ receiptId: "receipt", sourceEventId: "signal-without-subscribers", receivedAt: event.lastReceivedAt }] });
    const writeText = vi.fn().mockRejectedValueOnce(new Error("Denied")).mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });
    renderSection();
    const view = await screen.findByRole("button", { name: "View details for Activity store" });
    await act(async () => { fireEvent.click(view); });
    const dialog = await screen.findByRole("dialog");
    expect(await within(dialog).findByText("Active subscribers")).toBeVisible();
    expect(within(dialog).getByText("Total subscriptions")).toBeVisible();
    expect(within(dialog).getByRole("region", { name: "Received signals" })).toHaveTextContent("signal-without-subscribers");
    expect(within(dialog).getByRole("region", { name: "Automation deliveries" })).toHaveTextContent("No Automation deliveries");
    const json = within(dialog).getByLabelText("Example JSON body") as HTMLTextAreaElement;
    expect(JSON.parse(json.value).data).toEqual({ reference: "EXAMPLE-42" });
    fireEvent.click(within(dialog).getByRole("button", { name: "Copy JSON body" }));
    expect(await within(dialog).findByText("Copy failed. Select the example and copy it manually.")).toBeVisible();
    fireEvent.click(within(dialog).getByRole("button", { name: "Copy JSON body" }));
    await waitFor(() => expect(within(dialog).queryByText(/Copy failed/)).not.toBeInTheDocument());
    fireEvent.click(within(dialog).getByRole("tab", { name: "cURL request" }));
    const curl = within(dialog).getByLabelText("Example cURL request") as HTMLTextAreaElement;
    expect(curl.value).toContain(`/api/v1/hooks/${eventKey}`);
    expect(curl.value).toContain("Authorization: Bearer <secret>");
    expect(curl.value).not.toContain("\n+ ");
  });

  it("keeps clipboard failure and manual copy inside the one-time credential dialog", async () => {
    vi.mocked(listWebhookEvents).mockResolvedValue([]);
    vi.mocked(createWebhookEvent).mockResolvedValue({ eventId, eventKey, token: "one-time-token", status: "Active" });
    Object.assign(navigator, { clipboard: { writeText: vi.fn().mockRejectedValue(new Error("Denied")) } });
    renderSection();
    fireEvent.click(await screen.findByRole("button", { name: "New Event" }));
    fireEvent.change(await screen.findByLabelText("Event key"), { target: { value: eventKey } });
    fireEvent.change(await screen.findByLabelText("Event name"), { target: { value: "Store" } });
    fireEvent.click(screen.getByRole("button", { name: "Create Event" }));
    const dialog = (await screen.findByLabelText("Event credential")).closest('[role="dialog"]') as HTMLElement;
    fireEvent.click(within(dialog).getByRole("button", { name: "Copy credential" }));
    await waitFor(() => expect(within(dialog).getByText("Copy failed. Select the value and copy it manually.")).toBeVisible());
    expect(within(dialog).getByLabelText("Event credential")).toHaveValue("one-time-token");
    fireEvent.click(within(dialog).getByRole("button", { name: "Done" }));
    await waitFor(() => expect(screen.queryByLabelText("Event credential")).not.toBeInTheDocument());
    expect(screen.queryByText(/Copy failed/)).not.toBeInTheDocument();
  });

  it("retains a failed creation draft for retry and clears cancelled drafts", async () => {
    vi.mocked(listWebhookEvents).mockResolvedValue([]);
    vi.mocked(createWebhookEvent).mockRejectedValueOnce(new Error("Temporary failure"))
      .mockResolvedValueOnce({ eventId, eventKey, token: "retry-token", status: "Active" });
    renderSection();
    fireEvent.click(await screen.findByRole("button", { name: "New Event" }));
    const drawer = await screen.findByRole("dialog", { name: "New Event" });
    expect(within(drawer).getByRole("button", { name: "Create Event" })).toBeDisabled();
    fireEvent.change(within(drawer).getByLabelText("Event key"), { target: { value: eventKey } });
    fireEvent.change(within(drawer).getByLabelText("Event name"), { target: { value: " Retry Store " } });
    fireEvent.click(within(drawer).getByRole("button", { name: "Create Event" }));
    expect(await within(drawer).findByText("Temporary failure")).toBeVisible();
    expect(within(drawer).getByLabelText("Event name")).toHaveValue(" Retry Store ");
    fireEvent.click(within(drawer).getByRole("button", { name: "Create Event" }));
    const credential = (await screen.findByLabelText("Event credential")).closest('[role="dialog"]') as HTMLElement;
    expect(createWebhookEvent).toHaveBeenLastCalledWith("Retry Store", eventKey);
    fireEvent.click(within(credential).getByRole("button", { name: "Done" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: "New Event" }));
    fireEvent.change(screen.getByLabelText("Event key"), { target: { value: eventKey } });
    fireEvent.change(screen.getByLabelText("Event name"), { target: { value: "Discard me" } });
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: "New Event" }));
    expect(screen.getByLabelText("Event name")).toHaveValue("");
  });

});
