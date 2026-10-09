import { act, fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { AppRouter } from "./AppRouter";
import { navigateToAppPath } from "./appRoute";
import { navigateFromBrowserHistory, suspendLiveSessionForNavigation } from "../services/realtime";
vi.mock("../services/realtime", () => ({ navigateFromBrowserHistory: vi.fn(), suspendLiveSessionForNavigation: vi.fn().mockResolvedValue(undefined) }));
vi.mock("../features/admin/AdminApp", () => ({ AdminApp: () => <div>Admin</div> }));
vi.mock("../features/chat/ChatApp", () => ({ ChatApp: ({ returnToActivity }: { returnToActivity?: () => void }) => <div>Chat{ returnToActivity ? <button onClick={returnToActivity}>Back to Activity</button> : null }</div> }));
const instance = "019944af-00d1-7000-8000-000000000001";
beforeEach(() => { vi.clearAllMocks(); window.history.replaceState(null, "", `/admin/instances/${instance}/activity`); });
describe("Admin conversation navigation", () => {
  it("lets the mounted Chat bootstrap attach once and retains normal chat history navigation", () => {
    render(<AppRouter />);
    act(() => navigateToAppPath(`/c/${instance}`));
    expect(screen.getByText("Chat")).toBeVisible();
    expect(navigateFromBrowserHistory).not.toHaveBeenCalled();
    act(() => navigateToAppPath("/"));
    expect(navigateFromBrowserHistory).toHaveBeenCalledTimes(1);
    expect(suspendLiveSessionForNavigation).toHaveBeenCalled();
  });
  it("returns to the validated instance Activity route and retains its filter", () => {
    const returnTo = `/admin/instances/${instance}/activity/sessions?q=Planning`;
    window.history.replaceState(null, "", `/c/${instance}?${new URLSearchParams({ returnTo })}`);
    render(<AppRouter />); fireEvent.click(screen.getByRole("button", { name: "Back to Activity" }));
    expect(window.location.pathname + window.location.search).toBe(returnTo);
    expect(screen.getByText("Admin")).toBeVisible();
  });
  it("ignores external return targets", () => {
    window.history.replaceState(null, "", `/c/${instance}?${new URLSearchParams({ returnTo: "https://example.com/" })}`);
    render(<AppRouter />); expect(screen.queryByRole("button", { name: "Back to Activity" })).not.toBeInTheDocument();
  });
});
