import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { App } from "antd";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ResourceImportPanel } from "./resourceImportPanel";
import { bindAdminDraftResources, uploadAdminDraftResourceContent } from "../../services/adminApi";

vi.mock("../../services/adminApi", () => ({
  uploadAdminDraftResourceContent: vi.fn(),
  bindAdminDraftResources: vi.fn()
}));

function renderPanel(onBound = vi.fn()) {
  render(
    <App>
      <ResourceImportPanel
        draftId="draft-1"
        expectedRevision={4}
        existingPaths={[]}
        existingCount={0}
        existingBytes={0}
        disabled={false}
        onBound={onBound}
        onError={vi.fn()}
      />
    </App>
  );
  return onBound;
}

function chooseFiles(files: File[]) {
  const input = [...document.querySelectorAll('input[type="file"]')].find(
    (candidate) => !candidate.hasAttribute("webkitdirectory") && !candidate.hasAttribute("directory")
  );
  expect(input).toBeTruthy();
  fireEvent.change(input!, { target: { files } });
}

describe("ResourceImportPanel", () => {
  beforeEach(() => {
    vi.mocked(uploadAdminDraftResourceContent).mockReset();
    vi.mocked(bindAdminDraftResources).mockReset();
  });

  it("previews multiple files, keeps a corrected kind, and binds them once", async () => {
    const onBound = renderPanel();
    vi.mocked(uploadAdminDraftResourceContent)
      .mockResolvedValueOnce({ contentSha256: "hash-policy", byteLength: 6, mediaType: "text/markdown" })
      .mockResolvedValueOnce({ contentSha256: "hash-notes", byteLength: 5, mediaType: "text/plain" });
    vi.mocked(bindAdminDraftResources).mockResolvedValue({ revision: 5, items: [] });
    const policy = new File(["policy"], "policy.md", { type: "text/markdown" });
    const notes = new File(["notes"], "notes.txt", { type: "text/plain" });
    Object.defineProperty(policy, "webkitRelativePath", { value: "knowledge/policy.md" });
    Object.defineProperty(notes, "webkitRelativePath", { value: "misc/notes.txt" });

    chooseFiles([policy, notes]);

    expect(await screen.findByLabelText("Imported resource path 1")).toHaveValue("knowledge/policy.md");
    expect(screen.getByLabelText("Imported resource path 2")).toHaveValue("misc/notes.txt");
    expect(screen.getByText("Choose a kind.")).toBeInTheDocument();
    fireEvent.mouseDown(screen.getByLabelText("Imported resource kind 2"));
    const option = await waitFor(() => {
      const match = document.querySelector('.ant-select-item-option[title="Reference"]');
      expect(match).toBeTruthy();
      return match as HTMLElement;
    });
    fireEvent.mouseDown(option);
    fireEvent.click(option);

    fireEvent.click(screen.getByRole("button", { name: "Bind imported resources" }));
    await waitFor(() => {
      expect(bindAdminDraftResources).toHaveBeenCalledWith("draft-1", 4, [
        {
          logicalPath: "knowledge/policy.md",
          kind: "Knowledge",
          mediaType: "text/markdown",
          contentSha256: "hash-policy",
          byteLength: 6
        },
        {
          logicalPath: "misc/notes.txt",
          kind: "Reference",
          mediaType: "text/plain",
          contentSha256: "hash-notes",
          byteLength: 5
        }
      ]);
    });
    expect(uploadAdminDraftResourceContent).toHaveBeenCalledTimes(2);
    expect(onBound).toHaveBeenCalled();
  });

  it("does not bind when one content upload fails", async () => {
    renderPanel();
    vi.mocked(uploadAdminDraftResourceContent)
      .mockResolvedValueOnce({ contentSha256: "hash-policy", byteLength: 6, mediaType: "text/markdown" })
      .mockRejectedValueOnce(new Error("content store unavailable"));
    const policy = new File(["policy"], "policy.md");
    const notes = new File(["notes"], "notes.txt");
    Object.defineProperty(policy, "webkitRelativePath", { value: "knowledge/policy.md" });
    Object.defineProperty(notes, "webkitRelativePath", { value: "references/notes.txt" });

    chooseFiles([policy, notes]);
    fireEvent.click(await screen.findByRole("button", { name: "Bind imported resources" }));

    await waitFor(() => {
      expect(screen.getByText("content store unavailable")).toBeInTheDocument();
    });
    expect(bindAdminDraftResources).not.toHaveBeenCalled();
  });
});
