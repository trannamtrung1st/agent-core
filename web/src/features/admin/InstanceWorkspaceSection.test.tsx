import { App, ConfigProvider } from "antd";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { InstanceWorkspaceSection } from "./InstanceWorkspaceSection";
import { listAgentWorkspace, downloadAgentWorkspaceItem, deleteAgentWorkspaceItem, type AgentWorkspacePage } from "../../services/agentWorkspace";
vi.mock("../../services/agentWorkspace", () => ({ listAgentWorkspace: vi.fn(), downloadAgentWorkspaceItem: vi.fn(), deleteAgentWorkspaceItem: vi.fn() }));
const item = { itemId: "i1", agentInstanceId: "a", logicalPath: "/home/reports/store-review.md", contentType: "text/markdown", byteSize: 123,
  sha256Hex: "a".repeat(64), revision: 4, createdAt: "2026-10-06T00:00:00Z", updatedAt: "2026-10-06T00:00:00Z", sourceSessionId: "s1" };
const page: AgentWorkspacePage = { items: [item], usedBytes: 123, totalItems: 1, nextPath: null, maxFileBytes: 52428800, maxInstanceBytes: 262144000 };
const view = (id = "a", archived = false) => <ConfigProvider><App><InstanceWorkspaceSection instanceId={id} archived={archived} /></App></ConfigProvider>;
beforeEach(() => { vi.resetAllMocks(); vi.mocked(listAgentWorkspace).mockResolvedValue(page); vi.mocked(downloadAgentWorkspaceItem).mockResolvedValue(); vi.mocked(deleteAgentWorkspaceItem).mockResolvedValue(); });

describe("Agent Workspace inspection", () => {
  it("loads metadata, downloads and requires modal confirmation before guarded deletion", async () => {
    render(view()); await screen.findByText(item.logicalPath);
    expect(screen.getByText("123 B of 250.0 MB · 1 item")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: `Download ${item.logicalPath}` }));
    await waitFor(() => expect(downloadAgentWorkspaceItem).toHaveBeenCalledWith("a", item, expect.any(AbortSignal)));
    await waitFor(() => expect(screen.getByRole("button", { name: `Delete ${item.logicalPath}` })).toBeEnabled());
    fireEvent.click(screen.getByRole("button", { name: `Delete ${item.logicalPath}` }));
    expect(deleteAgentWorkspaceItem).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(deleteAgentWorkspaceItem).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: `Delete ${item.logicalPath}` }));
    fireEvent.click(await screen.findByRole("button", { name: "Delete file" }));
    await waitFor(() => expect(deleteAgentWorkspaceItem).toHaveBeenCalledWith("a", item));
  });
  it("shows folders, disables file downloads and confirms only empty-folder deletion", async () => {
    const folder = { ...item, itemId: "folder", logicalPath: "/home/empty", contentType: "inode/directory", byteSize: 0, directory: true };
    vi.mocked(listAgentWorkspace).mockResolvedValue({ ...page, items: [folder], usedBytes: 0 });
    render(view()); await screen.findByText(folder.logicalPath);
    expect(screen.getByText("Folder")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: `Download ${folder.logicalPath}` })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: `Delete ${folder.logicalPath}` }));
    fireEvent.click(await screen.findByRole("button", { name: "Delete folder" }));
    await waitFor(() => expect(deleteAgentWorkspaceItem).toHaveBeenCalledWith("a", folder));
    expect(downloadAgentWorkspaceItem).not.toHaveBeenCalled();
  });
  it("shows empty, loading and retry without a false empty state on failure", async () => {
    vi.mocked(listAgentWorkspace).mockRejectedValueOnce(new Error("Temporary failure"));
    render(view()); await screen.findByRole("button", { name: "Retry workspace" });
    expect(screen.queryByText("No workspace items yet")).not.toBeInTheDocument();
    vi.mocked(listAgentWorkspace).mockResolvedValue({ ...page, items: [], totalItems: 0, usedBytes: 0 });
    fireEvent.click(screen.getByRole("button", { name: "Retry workspace" }));
    await screen.findByText("No workspace items yet");
  });
  it("keeps archived downloads and disables durable deletion", async () => {
    render(view("a", true)); await screen.findByText(item.logicalPath);
    expect(screen.getByText("Archived workspace is read-only")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: `Delete ${item.logicalPath}` })).toBeDisabled();
    expect(screen.getByRole("button", { name: `Download ${item.logicalPath}` })).toBeEnabled();
  });
  it("discards an older instance's delayed response", async () => {
    let finish!: (p: AgentWorkspacePage) => void;
    vi.mocked(listAgentWorkspace).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
    const rendered = render(view("a")); expect(screen.getByLabelText("Loading workspace")).toBeInTheDocument();
    rendered.rerender(view("b")); await screen.findByText(item.logicalPath);
    finish({ ...page, items: [{ ...item, logicalPath: "/home/old-instance.txt" }] });
    await waitFor(() => expect(screen.queryByText("/home/old-instance.txt")).not.toBeInTheDocument());
  });
  it("keeps long paths accessible and supports search and no-match recovery", async () => {
    const name = "/home/" + "very-long-name-".repeat(8) + ".md";
    vi.mocked(listAgentWorkspace).mockResolvedValue({ ...page, items: [{ ...item, logicalPath: name }] });
    render(view()); expect(await screen.findByText(name)).toHaveAttribute("title", name);
    fireEvent.change(screen.getByRole("textbox", { name: "Search workspace items" }), { target: { value: "absent" } });
    await screen.findByText("No matching items");
    fireEvent.change(screen.getByRole("textbox", { name: "Search workspace items" }), { target: { value: "" } });
    await screen.findByRole("button", { name: `Download ${name}` });
  });
});
