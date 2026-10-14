import { Blob as NodeBlob } from "node:buffer";
import { readFileSync } from "node:fs";
import { downloadFile, readFile, resolveFile } from "../filePreview/fileSource";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { HistoryAttachmentView, PendingAttachmentView } from "./AttachmentPreview";


vi.mock("../filePreview/fileSource", async original => ({...await original<typeof import("../filePreview/fileSource")>(),downloadFile:vi.fn(),readFile:vi.fn(),resolveFile:vi.fn()}));
const png=readFileSync("e2e/fixtures/file-preview/image.png");
describe("AttachmentPreview", () => {
  beforeEach(() => {
    vi.clearAllMocks();vi.mocked(downloadFile).mockResolvedValue(undefined);
    vi.stubGlobal("URL", {
      createObjectURL: vi.fn(() => "blob:history"),
      revokeObjectURL: vi.fn()
    });
  });

  it("renders a file chip with kind and a download label", async () => {

    render(
      <HistoryAttachmentView
        sessionId="s1"
        file={{ attachmentId: "a1", displayName: "architecture.pdf", contentType: "application/pdf" }}
      />
    );
    const download = await screen.findByRole("button", { name: "Download architecture.pdf" });
    expect(readFile).not.toHaveBeenCalled();fireEvent.click(download);
    await waitFor(()=>expect(downloadFile).toHaveBeenCalledWith({kind:"attachment",sessionId:"s1",attachmentId:"a1"},expect.any(AbortSignal)));
    expect(screen.getByText("PDF")).toBeInTheDocument();
  });

  it("opens image attachments with meaningful alt text", async () => {
    vi.mocked(resolveFile).mockResolvedValue({displayName:"screenshot.png",contentType:"image/png",byteSize:png.length});
    vi.mocked(readFile).mockResolvedValue(new NodeBlob([png],{type:"image/png"}) as unknown as Blob);
    render(
      <HistoryAttachmentView
        sessionId="s1"
        file={{ attachmentId: "a2", displayName: "screenshot.png", contentType: "image/png" }}
      />
    );
    const image = await screen.findByRole("img", { name: "screenshot.png" });
    expect(image.closest("a")).toHaveAttribute("aria-label", "Open screenshot.png");
    expect(image.closest("a")).toHaveAttribute("target", "_blank");
    expect(image.closest("a")).not.toHaveAttribute("download");
  });

  it("renders compact filenames without previews and keeps retry/removal on failed uploads", () => {
    const onRetry = vi.fn();
    const onRemove = vi.fn();
    render(
      <PendingAttachmentView
        item={{
          localId: "l1",
          displayName: "clip.png",
          contentType: "image/png",
          byteSize: 4,
          status: "error",
          progress: 0,
          attachmentId: null,
          error: "Upload failed"
        }}
        onRetry={onRetry}
        onRemove={onRemove}
      />
    );
    expect(screen.queryByRole("img", { name: "clip.png" })).not.toBeInTheDocument();
    expect(screen.getByText("clip.png")).toHaveClass("single-line-ellipsis");
    expect(screen.getByText("Upload failed")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Retry clip.png" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry clip.png" }));
    fireEvent.click(screen.getByRole("button", { name: "Remove clip.png" }));
    expect(onRetry).toHaveBeenCalledOnce();
    expect(onRemove).toHaveBeenCalledOnce();
    expect(URL.createObjectURL).not.toHaveBeenCalled();
  });
});
