import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App, ConfigProvider } from "antd";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CredentialsSection, InstanceCredentialsSection } from "./CredentialsSection";
import * as api from "../../services/adminApi";

vi.mock("../../services/adminApi", () => ({ listCredentials: vi.fn(), createCredential: vi.fn(),
  updateCredential: vi.fn(), replaceCredentialValue: vi.fn(), deleteCredential: vi.fn(),
  listCredentialBindings: vi.fn(), bindCredential: vi.fn(), unbindCredential: vi.fn(), resetBrowserProfile: vi.fn() }));
const credential: api.SystemCredential = { credentialId: "credential", displayName: "Shared store", kind: "Password", status: "Active",
  metadata: { username: "operator@example.test", application: "store" }, allowedOrigins: ["https://store.example.test"],
  revision: 2, bindingCount: 1, createdAtUtc: "2026-10-07T00:00:00Z", updatedAtUtc: "2026-10-07T00:00:00Z" };
function view(child = <CredentialsSection />) { return render(<ConfigProvider><App>{child}</App></ConfigProvider>); }
beforeEach(() => { vi.clearAllMocks(); vi.mocked(api.listCredentials).mockResolvedValue([credential]); });
afterEach(cleanup);

describe("System credentials", () => {
  it("retains existing dynamic metadata when editing safe fields", async () => {
    vi.mocked(api.updateCredential).mockResolvedValue(credential);
    view(); fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
    const dialog = within(await screen.findByRole("dialog", { name: "Edit credential" }));
    await waitFor(() => expect(dialog.getByLabelText("Metadata key 1")).toHaveValue("username"));
    expect(dialog.getByLabelText("Metadata value 1")).toHaveValue("operator@example.test");
    expect(dialog.getByLabelText("Metadata key 2")).toHaveValue("application");
    expect(dialog.queryByLabelText("Protected value")).toBeNull();
    expect(dialog.queryByLabelText("Kind")).toBeNull();
    fireEvent.change(dialog.getByLabelText("Display name"), { target: { value: "Renamed shared store" } });
    fireEvent.click(dialog.getByRole("button", { name: "Save credential" }));
    await waitFor(() => expect(api.updateCredential).toHaveBeenCalledWith(credential, { displayName: "Renamed shared store", status: "Active",
      metadata: credential.metadata, allowedOrigins: credential.allowedOrigins }));
  });
  it("clears transient protected input after replacement failure and cancel", async () => {
    vi.mocked(api.replaceCredentialValue).mockRejectedValue(new Error("Revision conflict"));
    view(); fireEvent.click(await screen.findByRole("button", { name: "Replace value" }));
    const dialog = within(await screen.findByRole("dialog", { name: "Replace protected value" }));
    const input = dialog.getByLabelText("Protected value", { exact: true });
    expect(input).toHaveAttribute("type", "password");
    expect(dialog.queryByRole("img", { name: /eye/ })).toBeNull();
    fireEvent.change(input, { target: { value: "transient-test-value" } });
    fireEvent.click(dialog.getByRole("button", { name: "Save credential" }));
    await waitFor(() => expect(input).toHaveValue(""));
    expect(dialog.getByRole("alert")).toHaveTextContent("Revision conflict");
    fireEvent.change(input, { target: { value: "must-clear-on-cancel" } });
    fireEvent.click(dialog.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    fireEvent.click(screen.getByRole("button", { name: "Replace value" }));
    expect(within(await screen.findByRole("dialog", { name: "Replace protected value" })).getByLabelText("Protected value", { exact: true })).toHaveValue("");
  });
  it("shows a collection load failure and retries to an empty inventory", async () => {
    vi.mocked(api.listCredentials).mockRejectedValueOnce(new Error("Network unavailable")).mockResolvedValue([]);
    view(); expect(await screen.findByRole("alert")).toHaveTextContent("Network unavailable");
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));
    expect(await screen.findByText("No credentials yet")).toBeInTheDocument();
    expect(api.listCredentials).toHaveBeenCalledTimes(2);
  });
  it("lets archived owners inspect grants while disabling mutation and profile reset", async () => {
    vi.mocked(api.listCredentialBindings).mockResolvedValue([{ bindingId: "grant", credentialId: "credential", reference: "store-admin", revision: 1, credential }]);
    view(<InstanceCredentialsSection instanceId="owner" revision={2} archived />);
    expect(await screen.findByRole("cell", { name: "store-admin" })).toBeInTheDocument();
    for (const name of ["Bind credential", "Unbind", "Reset browser profile"]) expect(screen.getByRole("button", { name })).toBeDisabled();
    expect(screen.getByText("username: operator@example.test")).toBeInTheDocument();
  });
});
