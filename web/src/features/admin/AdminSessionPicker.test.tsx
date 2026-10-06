import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { AdminSessionPicker } from "./AdminSessionPicker";
import { getSession, listCatalog, type CatalogItem } from "../../services/api";
vi.mock("../../services/api", () => ({ getSession: vi.fn(), listCatalog: vi.fn() }));
const item = (sessionId: string, title: string): CatalogItem => ({ sessionId, title, agentId: "examiner", agentVersion: 1,
  status: "ended", ended: true, archived: false, workspaceOwned: false, runtimeEpoch: 0, revision: 1,
  createdAt: "2026-10-05T00:00:00Z", updatedAt: "2026-10-05T01:00:00Z" });
beforeEach(() => { vi.clearAllMocks(); vi.mocked(listCatalog).mockResolvedValue({ items: [item("conversation", "Practice interview")], nextCursor: null, hasMore: false }); });
async function open() { fireEvent.mouseDown(screen.getByRole("combobox", { name: "Source conversation" })); await screen.findByText(/Practice interview/); }
describe("Admin conversation sources", () => {
  it("browses by title and checks instance ownership before selection", async () => {
    const onChange = vi.fn(); vi.mocked(getSession).mockResolvedValue({ sessionId: "conversation", agentInstanceId: "instance" } as Awaited<ReturnType<typeof getSession>>);
    render(<AdminSessionPicker instanceId="instance" value="" onChange={onChange} />);
    await open(); fireEvent.click(document.querySelector(".ant-select-item-option")!);
    await waitFor(() => expect(onChange).toHaveBeenCalledWith("conversation"));
    expect(listCatalog).toHaveBeenCalledWith({ limit: 50, includeArchived: true, cursor: null });
  });
  it("rejects a different instance without discarding the current source", async () => {
    const onChange = vi.fn(); vi.mocked(getSession).mockResolvedValue({ sessionId: "conversation", agentInstanceId: "other-instance" } as Awaited<ReturnType<typeof getSession>>);
    render(<AdminSessionPicker instanceId="instance" value="previous-source" onChange={onChange} />);
    await open(); fireEvent.click(document.querySelector(".ant-select-item-option")!);
    expect(await screen.findByText(/belongs to another instance/)).toBeVisible();
    expect(onChange).not.toHaveBeenCalled(); expect(screen.getByText("Session ID: previous-source")).toBeVisible();
  });
  it("recovers a catalog failure and keeps manual entry available", async () => {
    vi.mocked(listCatalog).mockRejectedValueOnce(new Error("Catalog unavailable"));
    const onChange = vi.fn(); render(<AdminSessionPicker instanceId="instance" value="" onChange={onChange} />);
    fireEvent.mouseDown(screen.getByRole("combobox", { name: "Source conversation" }));
    expect(await screen.findByText("Catalog unavailable")).toBeVisible();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Retry" })); });
    await waitFor(() => expect(listCatalog).toHaveBeenCalledTimes(2));
    fireEvent.click(screen.getByRole("button", { name: "Enter Session ID" }));
    fireEvent.change(screen.getByRole("textbox", { name: "Session ID" }), { target: { value: "pasted-source" } });
    expect(onChange).toHaveBeenCalledWith("pasted-source");
  });
  it("loads older conversations explicitly without replacing the current page", async () => {
    vi.mocked(listCatalog).mockResolvedValueOnce({ items: [item("conversation", "Practice interview")], nextCursor: "older", hasMore: true })
      .mockResolvedValueOnce({ items: [item("older-chat", "Earlier interview")], nextCursor: null, hasMore: false });
    render(<AdminSessionPicker instanceId="instance" value="" onChange={vi.fn()} />);
    await open(); await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Load older conversations" })); });
    expect(listCatalog).toHaveBeenLastCalledWith({ limit: 50, includeArchived: true, cursor: "older" });
    expect(screen.queryByRole("button", { name: "Load older conversations" })).not.toBeInTheDocument();
    fireEvent.keyDown(screen.getByRole("combobox", { name: "Source conversation" }), { key: "ArrowDown", code: "ArrowDown", keyCode: 40 });
    await waitFor(() => {
      expect(screen.getByText(/Earlier interview/)).toBeVisible();
      expect(screen.getByText(/Practice interview/)).toBeVisible();
    });
  });
});
