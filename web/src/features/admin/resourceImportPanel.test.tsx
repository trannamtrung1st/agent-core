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

function fileInput(directory: boolean) {
  const input = [...document.querySelectorAll('input[type="file"]')].find((candidate) => {
    const isDirectory = candidate.hasAttribute("webkitdirectory") || candidate.hasAttribute("directory");
    return directory ? isDirectory : !isDirectory;
  });
  expect(input).toBeTruthy();
  return input as HTMLInputElement;
}

function chooseFiles(files: File[]) {
  fireEvent.change(fileInput(false), { target: { files } });
}

function chooseFolder(files: File[]) {
  fireEvent.change(fileInput(true), { target: { files } });
}

function withRelativePath(file: File, relativePath: string) {
  Object.defineProperty(file, "webkitRelativePath", { value: relativePath });
  return file;
}

describe("ResourceImportPanel", () => {
  beforeEach(() => {
    vi.mocked(uploadAdminDraftResourceContent).mockReset();
    vi.mocked(bindAdminDraftResources).mockReset();
  });

  it("previews chosen files without a package root and binds them once", async () => {
    const onBound = renderPanel();
    vi.mocked(uploadAdminDraftResourceContent)
      .mockResolvedValueOnce({ contentSha256: "hash-policy", byteLength: 6, mediaType: "text/markdown" })
      .mockResolvedValueOnce({ contentSha256: "hash-notes", byteLength: 5, mediaType: "text/plain" });
    vi.mocked(bindAdminDraftResources).mockResolvedValue({ revision: 5, items: [] });
    const policy = new File(["policy"], "policy.md", { type: "text/markdown" });
    const notes = new File(["notes"], "notes.txt", { type: "text/plain" });

    chooseFiles([policy, notes]);

    expect(await screen.findByLabelText("Imported resource path 1")).toHaveValue("policy.md");
    expect(screen.getByLabelText("Imported resource path 2")).toHaveValue("notes.txt");
    expect(screen.queryByText("Choose a kind.")).not.toBeInTheDocument();
    expect(screen.getByLabelText("Imported resource path 1")).toHaveAttribute("aria-invalid", "false");
    expect(screen.getByRole("button", { name: "Bind imported resources" })).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Imported resource path 1"), {
      target: { value: "knowledge/policy.md" }
    });
    fireEvent.change(screen.getByLabelText("Imported resource path 2"), {
      target: { value: "references/notes.txt" }
    });
    expect(screen.queryByText("Choose a kind.")).not.toBeInTheDocument();

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
          logicalPath: "references/notes.txt",
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

  it("keeps new selections neutral and validates only the field that has been left", async () => {
    renderPanel();
    chooseFiles([new File(["notes"], "notes.txt", { type: "text/plain" })]);
    const path = await screen.findByLabelText("Imported resource path 1");
    const kind = screen.getByLabelText("Imported resource kind 1");
    expect(path).toHaveAttribute("aria-invalid", "false");
    expect(kind).toHaveAttribute("aria-invalid", "false");
    expect(screen.queryByText("Choose a kind.")).not.toBeInTheDocument();
    fireEvent.focus(kind);
    fireEvent.blur(kind);
    expect(await screen.findByText("Choose a kind.")).toBeInTheDocument();
    expect(kind).toHaveAttribute("aria-invalid", "true");
    expect(path).toHaveAttribute("aria-invalid", "false");
    fireEvent.change(path, { target: { value: "../notes.txt" } });
    expect(screen.queryByText("Path must not contain traversal segments.")).not.toBeInTheDocument();
    fireEvent.blur(path);
    expect(await screen.findByText("Path must not contain traversal segments.")).toBeInTheDocument();
    expect(path).toHaveAttribute("aria-invalid", "true");
    fireEvent.change(path, { target: { value: "knowledge/notes.txt" } });
    expect(path).toHaveAttribute("aria-invalid", "false");
    expect(kind).toHaveAttribute("aria-invalid", "false");
    expect(screen.getByRole("button", { name: "Bind imported resources" })).toBeEnabled();
  });

  it("shows oversized file status without marking its valid path as erroneous", async () => {
    renderPanel();
    const file = new File(["large"], "large.log", { type: "text/plain" });
    Object.defineProperty(file, "size", { value: 35_907_384 });
    chooseFiles([file]);
    const input = await screen.findByLabelText("Imported resource path 1");
    expect(input).toHaveAttribute("aria-invalid", "false");
    const status = screen.getByText("File is larger than 8 MiB.");
    expect(status).not.toHaveClass("ant-typography-danger");
    expect(screen.getByText("34.2 MiB")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Bind imported resources" })).toBeDisabled();
  });

  it("strips the selected folder root and infers kind from the top-level directory", async () => {
    renderPanel();
    vi.mocked(uploadAdminDraftResourceContent).mockResolvedValue({
      contentSha256: "hash-policy",
      byteLength: 6,
      mediaType: "text/markdown"
    });
    vi.mocked(bindAdminDraftResources).mockResolvedValue({ revision: 5, items: [] });
    const policy = withRelativePath(
      new File(["policy"], "policy.md", { type: "text/markdown" }),
      "my-agent/knowledge/policy.md"
    );

    chooseFolder([policy]);

    expect(await screen.findByLabelText("Imported resource path 1")).toHaveValue("knowledge/policy.md");
    expect(screen.queryByText("Choose a kind.")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Bind imported resources" }));
    await waitFor(() => {
      expect(bindAdminDraftResources).toHaveBeenCalledWith("draft-1", 4, [
        expect.objectContaining({ logicalPath: "knowledge/policy.md", kind: "Knowledge" })
      ]);
    });
  });

  it("does not bind when one content upload fails", async () => {
    renderPanel();
    vi.mocked(uploadAdminDraftResourceContent)
      .mockResolvedValueOnce({ contentSha256: "hash-policy", byteLength: 6, mediaType: "text/markdown" })
      .mockRejectedValueOnce(new Error("content store unavailable"));
    const policy = withRelativePath(
      new File(["policy"], "policy.md", { type: "text/markdown" }),
      "my-agent/knowledge/policy.md"
    );
    const notes = withRelativePath(
      new File(["notes"], "notes.txt", { type: "text/plain" }),
      "my-agent/references/notes.txt"
    );

    chooseFolder([policy, notes]);
    fireEvent.click(await screen.findByRole("button", { name: "Bind imported resources" }));

    await waitFor(() => {
      expect(screen.getByText("content store unavailable")).toBeInTheDocument();
    });
    expect(bindAdminDraftResources).not.toHaveBeenCalled();
  });
});
