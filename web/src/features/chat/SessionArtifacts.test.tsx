import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { SessionArtifacts } from "./SessionArtifacts";
import { downloadArtifact, getArtifact, listArtifactPage } from "../../services/artifacts";

vi.mock("../../services/artifacts", async original => ({ ...await original<object>(),
  downloadArtifact: vi.fn(), getArtifact: vi.fn(), listArtifactPage: vi.fn() }));
const file = { artifactId: "file-1", sessionId: "child-1", displayName: "report.md", contentType: "text/markdown", byteSize: 20 };
beforeEach(() => {
  vi.mocked(listArtifactPage).mockReset().mockResolvedValue({ items: [file], nextCursor: null, hasMore: false });
  vi.mocked(getArtifact).mockReset().mockResolvedValue(file);
  vi.mocked(downloadArtifact).mockReset().mockResolvedValue();
});
describe("background Session files", () => {
  it("downloads from the child Session without opening a chat", async () => {
    render(<SessionArtifacts tenantId="tenant" sessionId="child-1" open />);
    fireEvent.click(await screen.findByRole("button", { name: "Download report.md" }));
    await waitFor(() => expect(downloadArtifact).toHaveBeenCalledWith("child-1", "file-1", expect.any(AbortSignal), "tenant"));
    expect(listArtifactPage).toHaveBeenCalledWith("child-1", undefined, undefined, "tenant");
  });
  it("retries list failures in place", async () => {
    vi.mocked(listArtifactPage).mockRejectedValueOnce(new Error("Files could not be loaded. Try again."));
    render(<SessionArtifacts tenantId="tenant" sessionId="child-1" open />);
    expect(await screen.findByText("Files could not be loaded. Try again.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByRole("button", { name: "Download report.md" })).toBeInTheDocument();
  });
  it("ignores a previous Session's late file page", async () => {
    let finish!: (value: { items: typeof file[]; nextCursor: null; hasMore: boolean }) => void;
    vi.mocked(listArtifactPage).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }))
      .mockResolvedValueOnce({ items: [], nextCursor: null, hasMore: false });
    const view = render(<SessionArtifacts tenantId="tenant" sessionId="child-1" open />);
    view.rerender(<SessionArtifacts tenantId="tenant" sessionId="child-2" open />);
    await screen.findByText("No files are available with your current access.");
    await act(() => finish({ items: [file], nextCursor: null, hasMore: false }));
    expect(screen.queryByText("report.md")).not.toBeInTheDocument();
  });
  it("resets ownership and ignores a late page when the Run filter changes in the same Session", async () => {
    let finish!: (value: { items: typeof file[]; nextCursor: null; hasMore: boolean }) => void;
    vi.mocked(listArtifactPage).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }))
      .mockResolvedValueOnce({ items: [], nextCursor: null, hasMore: false });
    const view = render(<SessionArtifacts tenantId="tenant" sessionId="child-1" agentRunId="run-1" open />);
    await waitFor(() => expect(listArtifactPage).toHaveBeenCalledWith("child-1", undefined, "run-1", "tenant"));
    view.rerender(<SessionArtifacts tenantId="tenant" sessionId="child-1" agentRunId="run-2" open />);
    await act(() => finish({ items: [file], nextCursor: null, hasMore: false }));
    await screen.findByText("No files are available with your current access.");
    expect(listArtifactPage).toHaveBeenCalledWith("child-1", undefined, "run-2", "tenant");
    expect(screen.queryByText("report.md")).not.toBeInTheDocument();
  });
});

it('never loads an unscoped file collection', () => {
  render(<SessionArtifacts sessionId="child-1" open />);
  expect(screen.getByText('Files are unavailable outside an organization.')).toBeVisible();
  expect(listArtifactPage).not.toHaveBeenCalled(); expect(getArtifact).not.toHaveBeenCalled();
});
