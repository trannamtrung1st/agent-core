import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { App as AntApp, ConfigProvider } from "antd";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BuiltinEventSubscribers } from "./BuiltinEventSubscribers";
import { instanceContinuityRequest, listAdminInstances, type AutomationReview } from "../../services/adminApi";
import { navigateToAppPath } from "../../app/appRoute";

vi.mock("../../services/adminApi", () => ({ listAdminInstances: vi.fn(), instanceContinuityRequest: vi.fn() }));
vi.mock("../../app/appRoute", async original => ({ ...await original<typeof import("../../app/appRoute")>(), navigateToAppPath: vi.fn() }));

const review = (name: string): AutomationReview => ({ items: [{ automationId: name, name, enabled: true,
  triggers: [{ triggerId: name, revision: 1, enabled: true, kind: "event", source: { kind: "builtin", key: "run.failed" } }]
}] as AutomationReview["items"] });
function view(instanceId?: string) {
  return <ConfigProvider theme={{ token: { motion: false } }}><AntApp><BuiltinEventSubscribers eventKey="run.failed" instanceId={instanceId} /></AntApp></ConfigProvider>;
}

describe("Built-in subscriber scope and recovery", () => {
  beforeEach(() => { vi.mocked(listAdminInstances).mockReset(); vi.mocked(instanceContinuityRequest).mockReset(); vi.mocked(navigateToAppPath).mockReset(); });
  it("clears an inventory error after retry without requiring an Instance selection", async () => {
    vi.mocked(listAdminInstances).mockRejectedValueOnce(new Error("offline")).mockResolvedValueOnce([]);
    render(view());
    fireEvent.click(await screen.findByRole("button", { name: "Retry Instances" }));
    await waitFor(() => expect(screen.queryByRole("alert")).not.toBeInTheDocument());
    expect(listAdminInstances).toHaveBeenCalledTimes(2);
    expect(instanceContinuityRequest).not.toHaveBeenCalled();
  });
  it("uses the current embedded owner and ignores a late previous-owner read", async () => {
    let release!: (value: AutomationReview) => void;
    vi.mocked(instanceContinuityRequest).mockImplementation(id => id === "first" ? new Promise(resolve => { release = resolve; }) : Promise.resolve(review("Second automation")));
    const rendered = render(view("first"));
    await waitFor(() => expect(instanceContinuityRequest).toHaveBeenCalledWith("first", "automations"));
    rendered.rerender(view("second"));
    const second = await screen.findByRole("button", { name: "Second automation" });
    await act(async () => release(review("First automation")));
    expect(screen.queryByRole("button", { name: "First automation" })).not.toBeInTheDocument();
    fireEvent.click(second);
    expect(navigateToAppPath).toHaveBeenCalledWith("/admin/instances/second/automation/automations?automation=Second automation");
    expect(listAdminInstances).not.toHaveBeenCalled();
  });
  it("retries a failed owned subscription read and restores the exact Automation link", async () => {
    vi.mocked(instanceContinuityRequest).mockRejectedValueOnce(new Error("offline")).mockResolvedValueOnce(review("Recovered"));
    render(view("owner"));
    fireEvent.click(await screen.findByRole("button", { name: "Retry subscribers" }));
    fireEvent.click(await screen.findByRole("button", { name: "Recovered" }));
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(navigateToAppPath).toHaveBeenCalledWith("/admin/instances/owner/automation/automations?automation=Recovered");
  });
});
