import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App as AntApp, ConfigProvider } from "antd";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BrowserPrivacySection } from "./BrowserPrivacySection";
import { getBrowserPrivacy, saveBrowserPrivacy, type BrowserPrivacy } from "../../services/adminApi";

vi.mock("../../services/adminApi", () => ({ getBrowserPrivacy: vi.fn(), saveBrowserPrivacy: vi.fn() }));
const policy: BrowserPrivacy = { saved: { mode: "Protected", revision: 0, unmaskedOrigins: [], trustedGraphicsOrigins: [] },
  effective: { mode: "Protected", revision: 0, unmaskedOrigins: [], trustedGraphicsOrigins: [] },
  deployment: { captureAllowed: true, unmaskedAllowed: true, unmaskedOriginCeiling: ["https://example.test"], graphicsOriginCeiling: ["https://example.test"] },
  restartRequired: false, activation: "Saved changes activate after host restart.", durable: true, constrainedByDeployment: false };
function mount() { return render(<ConfigProvider theme={{ token: { motion: false } }}><AntApp><BrowserPrivacySection /></AntApp></ConfigProvider>); }
async function choose(label: string, option: string) {
  fireEvent.mouseDown(screen.getByRole("combobox", { name: label }));
  fireEvent.click(await screen.findByText(option, { selector: ".ant-select-item-option-content" }));
}
describe("BrowserPrivacySection", () => {
  beforeEach(() => { vi.mocked(getBrowserPrivacy).mockReset().mockResolvedValue(structuredClone(policy)); vi.mocked(saveBrowserPrivacy).mockReset(); });
  it("reports same-revision deployment constraints without calling the saved policy active or asking for restart", async () => {
    vi.mocked(getBrowserPrivacy).mockResolvedValue({ ...policy, saved: { ...policy.saved, mode: "Unmasked", revision: 3 },
      effective: { ...policy.effective, revision: 3 }, constrainedByDeployment: true,
      activation: "The saved policy is constrained by deployment restrictions. Restarting alone cannot remove those restrictions." });
    mount();
    expect(await screen.findByText("Unmasked · revision 3 · constrained by deployment")).toBeVisible();
    expect(screen.queryByText(/revision 3 · active/)).not.toBeInTheDocument();
    expect(screen.queryByText(/revision 3 · restart required/)).not.toBeInTheDocument();
    expect(screen.getByText(/Restarting alone cannot remove/)).toBeVisible();
  });
  it("saves Disabled and keeps saved/effective revisions distinct", async () => {
    vi.mocked(saveBrowserPrivacy).mockResolvedValue({ ...policy, saved: { ...policy.saved, mode: "Disabled", revision: 1 }, restartRequired: true });
    mount(); await screen.findByText("Protected · revision 0 · active as saved");
    await choose("Saved screenshot privacy mode", "Disabled — no screenshots");
    fireEvent.click(screen.getByRole("button", { name: "Save screenshot privacy" }));
    await screen.findByText("Disabled · revision 1 · restart required");
    expect(screen.getByText("Protected · revision 0")).toBeVisible();
    expect(saveBrowserPrivacy).toHaveBeenCalledWith(expect.objectContaining({ mode: "Disabled", expectedRevision: 0, acknowledgeExposure: false }), expect.any(AbortSignal));
  });
  it("requires an exact origin and explicit modal acknowledgement for every Unmasked save", async () => {
    vi.mocked(saveBrowserPrivacy).mockResolvedValue({ ...policy, saved: { ...policy.saved, mode: "Unmasked", unmaskedOrigins: ["https://example.test"], revision: 1 }, restartRequired: true });
    mount(); await screen.findByText("Protected · revision 0 · active as saved");
    await choose("Saved screenshot privacy mode", "Unmasked — explicit confidentiality exception");
    fireEvent.click(screen.getByRole("button", { name: "Save screenshot privacy" }));
    await waitFor(() => expect(screen.getByText("Select at least one deployment-approved exact origin.")).toBeVisible());
    expect(saveBrowserPrivacy).not.toHaveBeenCalled();
    await choose("Trusted exact origins for Unmasked capture", "https://example.test");
    fireEvent.click(screen.getByRole("button", { name: "Save screenshot privacy" }));
    const dialog = await screen.findByRole("dialog");
    await waitFor(() => expect(within(dialog).getByText(/Unmasked screenshots can expose credentials/)).toBeVisible());
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(saveBrowserPrivacy).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Save screenshot privacy" }));
    const confirmation = await screen.findByRole("dialog");
    await waitFor(() => expect(within(confirmation).getByRole("button", { name: "Acknowledge exposure and save" })).toBeVisible());
    fireEvent.click(within(confirmation).getByRole("button", { name: "Acknowledge exposure and save" }));
    await screen.findByText("Unmasked · revision 1 · restart required");
    expect(saveBrowserPrivacy).toHaveBeenCalledWith(expect.objectContaining({ mode: "Unmasked", acknowledgeExposure: true, unmaskedOrigins: ["https://example.test"] }), expect.any(AbortSignal));
  });
  it("retains failed edits and reloads the current revision before a conflict retry", async () => {
    vi.mocked(saveBrowserPrivacy).mockRejectedValueOnce(new Error("Browser privacy changed. Reload the saved policy before retrying."));
    mount(); await screen.findByText("Protected · revision 0 · active as saved");
    await choose("Saved screenshot privacy mode", "Disabled — no screenshots");
    fireEvent.click(screen.getByRole("button", { name: "Save screenshot privacy" }));
    await screen.findByText(/Browser privacy changed/);
    expect(screen.getByRole("combobox", { name: "Saved screenshot privacy mode" }).closest(".ant-select")).toHaveTextContent("Disabled — no screenshots");
    vi.mocked(getBrowserPrivacy).mockResolvedValue({ ...policy, saved: { ...policy.saved, revision: 2 }, restartRequired: true });
    fireEvent.click(screen.getByRole("button", { name: "Reload saved privacy policy" }));
    await screen.findByText("Protected · revision 2 · restart required");
    await choose("Saved screenshot privacy mode", "Disabled — no screenshots");
    vi.mocked(saveBrowserPrivacy).mockResolvedValue({ ...policy, saved: { ...policy.saved, mode: "Disabled", revision: 3 }, restartRequired: true });
    fireEvent.click(screen.getByRole("button", { name: "Save screenshot privacy" }));
    await screen.findByText("Disabled · revision 3 · restart required");
    expect(saveBrowserPrivacy).toHaveBeenLastCalledWith(expect.objectContaining({ expectedRevision: 2 }), expect.any(AbortSignal));
  });
  it("retries a failed read and prevents a late load after unmount", async () => {
    vi.mocked(getBrowserPrivacy).mockRejectedValueOnce(new Error("Temporary failure"));
    const view = mount(); await screen.findByText("Temporary failure");
    fireEvent.click(screen.getByRole("button", { name: "Reload saved privacy policy" }));
    await screen.findByText("Protected · revision 0 · active as saved");
    view.unmount(); expect(vi.mocked(getBrowserPrivacy).mock.calls.at(-1)?.[0]?.aborted).toBe(true);
  });
});
