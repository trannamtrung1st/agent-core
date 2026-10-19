import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App as AntApp, ConfigProvider } from "antd";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BrowserPrivacySection } from "./BrowserPrivacySection";
import { getBrowserPrivacy, saveBrowserPrivacy, type BrowserPrivacy } from "../../services/systemBrowserPrivacy";
import { IdentityError, IdentityCompletionError } from "../../services/identity";
import { approveAppNavigation } from "../../app/appRoute";

vi.mock("../../services/systemBrowserPrivacy", () => ({ getBrowserPrivacy: vi.fn(), saveBrowserPrivacy: vi.fn() }));
const policy: BrowserPrivacy = { saved: { mode: "Protected", revision: 0, unmaskedOrigins: [], trustedGraphicsOrigins: [] },
  effective: { mode: "Protected", revision: 0, unmaskedOrigins: [], trustedGraphicsOrigins: [] },
  deployment: { captureAllowed: true, unmaskedAllowed: true, unmaskedOriginCeiling: ["https://example.test"], graphicsOriginCeiling: ["https://example.test"] },
  restartRequired: false, activation: "Saved changes activate after host restart.", durable: true, constrainedByDeployment: false };
function mount() { return render(<ConfigProvider theme={{ token: { motion: false } }}><AntApp><BrowserPrivacySection /></AntApp></ConfigProvider>); }
async function choose(label: string, option: string) {
  fireEvent.mouseDown(screen.getByRole("combobox", { name: label }));
  fireEvent.click(await screen.findByText(option, { selector: ".ant-select-item-option-content" }));
  await waitFor(() => expect(screen.getByRole("button", {name:"Save screenshot privacy"})).toBeEnabled());
}
describe("BrowserPrivacySection", () => {
  beforeEach(() => { vi.mocked(getBrowserPrivacy).mockReset().mockResolvedValue(structuredClone(policy)); vi.mocked(saveBrowserPrivacy).mockReset(); });
  it("keeps retained edits on Stay and restores saved fields and unload state on Discard", async () => {
    mount(); await screen.findByText("Protected · revision 0 · active as saved");
    await choose("Saved screenshot privacy mode", "Disabled — no screenshots");
    await waitFor(() => { const before = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(before); expect(before.defaultPrevented).toBe(true); });
    let leave!: Promise<boolean>;
    act(() => { leave = approveAppNavigation('/admin/instances'); });
    fireEvent.click(within(await screen.findByRole('dialog', { name: 'Discard unsaved changes?' })).getByRole('button', { name: 'Stay' }));
    expect(await leave).toBe(false);
    expect(screen.getByRole('combobox', { name: 'Saved screenshot privacy mode' }).closest('.ant-select')).toHaveTextContent('Disabled — no screenshots');
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Discard unsaved changes?' })).not.toBeInTheDocument());
    act(() => { leave = approveAppNavigation('/admin/definitions'); });
    fireEvent.click(within(await screen.findByRole('dialog', { name: 'Discard unsaved changes?' })).getByRole('button', { name: 'Discard and leave' }));
    expect(await leave).toBe(true);
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Saved screenshot privacy mode' }).closest('.ant-select')).toHaveTextContent('Protected — mask sensitive pixels'));
    const after = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(after);
    expect(after.defaultPrevented).toBe(false);
    expect(saveBrowserPrivacy).not.toHaveBeenCalled(); expect(getBrowserPrivacy).toHaveBeenCalledTimes(1);
  });
  it("restores saved origin selections and clears a failed-save error when discarding", async () => {
    vi.mocked(getBrowserPrivacy).mockResolvedValue({ ...policy, saved: { ...policy.saved, trustedGraphicsOrigins: ['https://example.test'] } });
    vi.mocked(saveBrowserPrivacy).mockRejectedValue(new Error('Temporary privacy write failure'));
    mount(); await screen.findByText('Protected · revision 0 · active as saved');
    await choose('Trusted graphics origins in Protected mode', 'https://example.test');
    await choose('Saved screenshot privacy mode', 'Disabled — no screenshots');
    fireEvent.click(screen.getByRole('button', { name: 'Save screenshot privacy' }));
    await screen.findByText('The service could not be reached. Check your connection and try again.');
    let leave!: Promise<boolean>; act(() => { leave = approveAppNavigation('/admin/instances'); });
    fireEvent.click(within(await screen.findByRole('dialog', { name: 'Discard unsaved changes?' })).getByRole('button', { name: 'Discard and leave' }));
    expect(await leave).toBe(true);
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Saved screenshot privacy mode' }).closest('.ant-select')).toHaveTextContent('Protected — mask sensitive pixels'));
    expect(screen.getByRole('combobox', { name: 'Trusted graphics origins in Protected mode' }).closest('.ant-select')).toHaveTextContent('https://example.test');
    expect(screen.queryByText('The service could not be reached. Check your connection and try again.')).not.toBeInTheDocument();
    expect(saveBrowserPrivacy).toHaveBeenCalledTimes(1);
  });
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
    await waitFor(() => expect(within(confirmation).getByRole("button", { name: "Acknowledge & save" })).toBeVisible());
    fireEvent.click(within(confirmation).getByRole("button", { name: "Acknowledge & save" }));
    await screen.findByText("Unmasked · revision 1 · restart required");
    expect(saveBrowserPrivacy).toHaveBeenCalledWith(expect.objectContaining({ mode: "Unmasked", acknowledgeExposure: true, unmaskedOrigins: ["https://example.test"] }), expect.any(AbortSignal));
  });
  it("retains failed edits and reloads the current revision before a conflict retry", async () => {
    vi.mocked(saveBrowserPrivacy).mockRejectedValueOnce(new Error("Browser privacy changed. Reload the saved policy before retrying."));
    mount(); await screen.findByText("Protected · revision 0 · active as saved");
    await choose("Saved screenshot privacy mode", "Disabled — no screenshots");
    fireEvent.click(screen.getByRole("button", { name: "Save screenshot privacy" }));
    await screen.findByText("Read current policy before saving again");
    expect(screen.getByRole("combobox", { name: "Saved screenshot privacy mode" }).closest(".ant-select")).toHaveTextContent("Disabled — no screenshots");
    vi.mocked(getBrowserPrivacy).mockResolvedValue({ ...policy, saved: { ...policy.saved, revision: 2 }, restartRequired: true });
    fireEvent.click(screen.getByRole("button", { name: "Reload saved policy" }));
    await screen.findByText("Protected · revision 2 · restart required");
    await choose("Saved screenshot privacy mode", "Disabled — no screenshots");
    vi.mocked(saveBrowserPrivacy).mockResolvedValue({ ...policy, saved: { ...policy.saved, mode: "Disabled", revision: 3 }, restartRequired: true });
    fireEvent.click(screen.getByRole("button", { name: "Save screenshot privacy" }));
    await screen.findByText("Disabled · revision 3 · restart required");
    expect(saveBrowserPrivacy).toHaveBeenLastCalledWith(expect.objectContaining({ expectedRevision: 2 }), expect.any(AbortSignal));
  });
  it("retries a failed read and prevents a late load after unmount", async () => {
    vi.mocked(getBrowserPrivacy).mockRejectedValueOnce(new Error("Temporary failure"));
    const view = mount(); await screen.findByText("The service could not be reached. Check your connection and try again.");
    fireEvent.click(screen.getByRole("button", { name: "Reload saved policy" }));
    await screen.findByText("Protected · revision 0 · active as saved");
    view.unmount(); expect(vi.mocked(getBrowserPrivacy).mock.calls.at(-1)?.[0]?.aborted).toBe(true);
  });
  it("blocks a lost save reply until explicit readback without resubmitting the policy", async () => {
    vi.mocked(saveBrowserPrivacy).mockRejectedValue(new IdentityCompletionError());
    mount(); await screen.findByText("Protected · revision 0 · active as saved"); await choose("Saved screenshot privacy mode", "Disabled — no screenshots");
    fireEvent.click(screen.getByRole("button", {name:"Save screenshot privacy"})); await screen.findByText("Read current policy before saving again");
    expect(screen.getByRole("button", {name:"Save screenshot privacy"})).toBeDisabled();
    vi.mocked(getBrowserPrivacy).mockResolvedValue({...policy,saved:{...policy.saved,mode:"Disabled",revision:1},restartRequired:true});
    fireEvent.click(screen.getByRole("button", {name:"Reload saved policy"})); await screen.findByText("Disabled · revision 1 · restart required");
    expect(saveBrowserPrivacy).toHaveBeenCalledTimes(1); expect(screen.getByRole("button", {name:"Save screenshot privacy"})).toBeDisabled();
  });
  it("clears policy and origin controls after platform access is denied and retains a deliberate reload action", async () => {
    vi.mocked(saveBrowserPrivacy).mockRejectedValue(new IdentityError(403,"permission_denied"));
    mount(); await screen.findByText("Protected · revision 0 · active as saved"); await choose("Saved screenshot privacy mode", "Disabled — no screenshots");
    fireEvent.click(screen.getByRole("button", {name:"Save screenshot privacy"})); await screen.findByText("Read current policy before saving again");
    expect(screen.queryByRole("combobox")).not.toBeInTheDocument(); expect(screen.queryByText("Effective privacy")).not.toBeInTheDocument();
    expect(screen.getByRole("button", {name:"Reload saved policy"})).toBeEnabled(); expect(saveBrowserPrivacy).toHaveBeenCalledTimes(1);
  });

});
