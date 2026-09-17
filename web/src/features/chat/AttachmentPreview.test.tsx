import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { fetchAttachmentBlob, pendingPreviewUrl } from "../../services/attachments";
import { HistoryAttachmentView, PendingAttachmentView } from "./AttachmentPreview";

vi.mock("../../services/attachments", () => ({
  fetchAttachmentBlob: vi.fn(),
  pendingPreviewUrl: vi.fn()
}));

describe("AttachmentPreview", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal("URL", {
      createObjectURL: vi.fn(() => "blob:history"),
      revokeObjectURL: vi.fn()
    });
  });

  it("renders a file chip with kind and a download label", async () => {
    vi.mocked(fetchAttachmentBlob).mockResolvedValue(new Blob(["x"], { type: "application/pdf" }));
    render(
      <HistoryAttachmentView
        sessionId="s1"
        file={{ attachmentId: "a1", displayName: "architecture.pdf", contentType: "application/pdf" }}
      />
    );
    const link = await screen.findByRole("link", { name: "Download architecture.pdf" });
    expect(link).toHaveAttribute("download", "architecture.pdf");
    expect(screen.getByText("PDF")).toBeInTheDocument();
  });

  it("opens image attachments with meaningful alt text", async () => {
    vi.mocked(fetchAttachmentBlob).mockResolvedValue(new Blob([new Uint8Array([1])], { type: "image/png" }));
    render(
      <HistoryAttachmentView
        sessionId="s1"
        file={{ attachmentId: "a2", displayName: "screenshot.png", contentType: "image/png" }}
      />
    );
    const image = await screen.findByRole("img", { name: "screenshot.png" });
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
