import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { App as AntApp } from "antd";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { EventSubscriptionsSection } from "./EventSubscriptionsSection";

vi.mock("../../services/adminApi", () => ({
  listEventSources: vi.fn(),
  listEventSubscriptions: vi.fn(),
  createEventSubscription: vi.fn()
}));

import { createEventSubscription, listEventSources, listEventSubscriptions } from "../../services/adminApi";

const instanceId = "019944af-00d1-7000-8000-000000000001";
const sourceId = "22222222-2222-2222-2222-222222222222";

describe("EventSubscriptionsSection", () => {
  beforeEach(() => {
    vi.mocked(listEventSources).mockReset();
    vi.mocked(listEventSubscriptions).mockReset();
    vi.mocked(createEventSubscription).mockReset();
  });

  it("subscribes the instance without creating an application connection", async () => {
    vi.mocked(listEventSources).mockResolvedValue([
      {
        sourceId,
        displayName: "Demo Store",
        kind: "Webhook",
        sourceKey: "11111111-1111-1111-1111-111111111111",
        status: "Active",
        revision: 1
      }
    ]);
    vi.mocked(listEventSubscriptions).mockResolvedValue([]);
    vi.mocked(createEventSubscription).mockResolvedValue({
      registrationId: "33333333-3333-3333-3333-333333333333",
      sourceId,
      eventType: "order.placed",
      status: "Active",
      revision: 1
    });
    render(
      <AntApp>
        <EventSubscriptionsSection instanceId={instanceId} />
      </AntApp>
    );
    expect(await screen.findByText("No order.placed subscription yet.")).toBeInTheDocument();
    expect(screen.getByText(/does not connect the agent/i)).toBeInTheDocument();
    fireEvent.mouseDown(screen.getByRole("combobox", { name: "Event source" }));
    fireEvent.click(await screen.findByText("Demo Store · Active"));
    fireEvent.click(screen.getByRole("button", { name: "Subscribe to order.placed" }));
    await waitFor(() => expect(createEventSubscription).toHaveBeenCalledWith(instanceId, sourceId));
    expect(await screen.findByText("order.placed")).toBeInTheDocument();
    expect(screen.queryByText("No order.placed subscription yet.")).not.toBeInTheDocument();
  });

  it("does not offer a revoked event source", async () => {
    vi.mocked(listEventSources).mockResolvedValue([
      {
        sourceId,
        displayName: "Demo Store",
        kind: "Webhook",
        sourceKey: "11111111-1111-1111-1111-111111111111",
        status: "Revoked",
        revision: 2
      }
    ]);
    vi.mocked(listEventSubscriptions).mockResolvedValue([]);
    render(
      <AntApp>
        <EventSubscriptionsSection instanceId={instanceId} />
      </AntApp>
    );
    expect(await screen.findByText("No active event source.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Subscribe to order.placed" })).not.toBeInTheDocument();
  });
  it("refreshes and focuses the exact subscription when returning from its run", async () => {
    vi.mocked(listEventSources).mockResolvedValue([{ sourceId: "store", displayName: "Store", status: "Active", kind: "Webhook", sourceKey: "key", revision: 1 }]);
    vi.mocked(listEventSubscriptions).mockResolvedValue([]);
    Object.defineProperty(HTMLElement.prototype, "scrollIntoView", { configurable: true, value: vi.fn() });
    const view = render(<AntApp><EventSubscriptionsSection instanceId="instance" /></AntApp>);
    await screen.findByText("No order.placed subscription yet.");
    vi.mocked(listEventSubscriptions).mockResolvedValue([{ registrationId: "target", sourceId: "store", eventType: "order.placed", status: "Active", revision: 1 }]);
    view.rerender(<AntApp><EventSubscriptionsSection instanceId="instance" selection={{ registrationId: "target", request: 1 }} /></AntApp>);
    await screen.findByText("order.placed");
    const row = document.querySelector('[data-event-registration-id="target"]');
    await waitFor(() => expect(row).toHaveFocus());
    expect(row).toHaveClass("admin-selected-source");
  });
});