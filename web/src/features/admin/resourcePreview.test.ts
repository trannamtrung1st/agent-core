import { describe, expect, it } from "vitest";
import {
  createPreviewItem,
  inferResourceKind,
  packageLogicalPath,
  readDroppedResourceFiles,
  resourceBatchLimitProblem,
  resourcePreviewProblem,
  MAX_RESOURCE_AGGREGATE_BYTES,
  MAX_RESOURCE_ITEM_BYTES,
  MAX_RESOURCE_ITEMS
} from "./resourcePreview";

function fileWithPath(name: string, relativePath: string, contents = "hello", type = ""): File {
  const file = new File([contents], name, { type });
  Object.defineProperty(file, "webkitRelativePath", { value: relativePath });
  return file;
}

describe("resourcePreview", () => {
  it("infers kind from a folder name and keeps other paths unset", () => {
    expect(inferResourceKind("knowledge/policy.md")).toBe("Knowledge");
    expect(packageLogicalPath("my-agent/knowledge/refund.md")).toBe("knowledge/refund.md");
    expect(inferResourceKind(packageLogicalPath("my-agent/templates/welcome.txt"))).toBe("Template");
    expect(inferResourceKind("pack/templates/welcome.txt")).toBeNull();
    expect(inferResourceKind("references/notes.md")).toBe("Reference");
    expect(inferResourceKind("assets/logo.png")).toBe("StaticAsset");
    expect(inferResourceKind("eval/case.json")).toBe("EvalFixture");
    expect(inferResourceKind("notes/readme.md")).toBeNull();
  });

  it("preserves a folder-relative path and reports local problems", () => {
    const item = createPreviewItem(fileWithPath("policy.md", "my-agent/knowledge/policy.md", "hello", "text/markdown"));
    expect(item.logicalPath).toBe("knowledge/policy.md");
    expect(item.kind).toBe("Knowledge");
    expect(item.mediaType).toBe("text/markdown");
    expect(resourcePreviewProblem(item, [item.logicalPath], [])).toBeNull();

    const traversal = { ...item, logicalPath: "../secrets.txt" };
    expect(resourcePreviewProblem(traversal, [traversal.logicalPath], [])).toMatch(/traversal/);
    const duplicate = resourcePreviewProblem(item, [item.logicalPath, item.logicalPath], []);
    expect(duplicate).toMatch(/duplicated/);
    const corrected = { ...item, logicalPath: "notes/readme.md", kind: "Reference" };
    expect(resourcePreviewProblem(corrected, [corrected.logicalPath], [])).toBeNull();
  });

  it("reports item, count, and aggregate limits before bind", () => {
    const item = createPreviewItem(fileWithPath("policy.md", "knowledge/policy.md"));
    expect(resourcePreviewProblem({ ...item, byteLength: MAX_RESOURCE_ITEM_BYTES + 1 }, [item.logicalPath], [])).toMatch(/8 MiB/);
    expect(resourceBatchLimitProblem(Array.from({ length: MAX_RESOURCE_ITEMS }, () => item), 1, 0)).toMatch(/64 resources/);
    expect(resourceBatchLimitProblem([{ byteLength: 1 }], 0, MAX_RESOURCE_AGGREGATE_BYTES)).toMatch(/64 MiB/);
  });

  it("reads a dropped folder entry with its relative path", async () => {
    const file = new File(["policy"], "policy.md", { type: "text/markdown" });
    const entry = {
      isFile: true,
      isDirectory: false,
      name: "policy.md",
      fullPath: "/pack/knowledge/policy.md",
      file: (success: (value: File) => void) => success(file)
    };
    const dropped = await readDroppedResourceFiles({
      items: [{ webkitGetAsEntry: () => entry }],
      files: []
    } as unknown as DataTransfer);
    expect(dropped).toHaveLength(1);
    expect((dropped[0] as File & { webkitRelativePath?: string }).webkitRelativePath).toBe("pack/knowledge/policy.md");
    expect(inferResourceKind(packageLogicalPath("pack/knowledge/policy.md"))).toBe("Knowledge");
  });
});
