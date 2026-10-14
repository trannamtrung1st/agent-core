import { Blob as NodeBlob } from "node:buffer";
import { readFileSync } from "node:fs";
import { downloadFile, readFile, resolveFile } from "../filePreview/fileSource";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { pendingPreviewUrl } from "../../services/attachments";
import { HistoryAttachmentView, PendingAttachmentView } from "./AttachmentPreview";

vi.mock("../../services/attachments", () => ({
  pendingPreviewUrl: vi.fn()
}));

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

  it("labels pending image previews and keeps retry on failed uploads", () => {
    vi.mocked(pendingPreviewUrl).mockReturnValue("blob:preview");
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
    expect(screen.getByRole("img", { name: "clip.png" })).toHaveAttribute("src", "blob:preview");
    expect(screen.getByText("Upload failed")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Retry clip.png" })).toBeInTheDocument();
  });
});
