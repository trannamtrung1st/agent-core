import { describe, expect, it } from "vitest";
import {
  createPreviewItem,
  resourceMediaType,
  isTextualResource,
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

  it("reads a dropped package folder and strips the package root", async () => {
    const file = new File(["hello"], "a.txt", { type: "text/plain" });
    const entry = {
      isFile: true,
      isDirectory: false,
      name: "a.txt",
      fullPath: "/root/templates/a.txt",
      file: (success: (value: File) => void) => success(file)
    };
    const dropped = await readDroppedResourceFiles({
      items: [{ webkitGetAsEntry: () => entry }],
      files: []
    } as unknown as DataTransfer);
    expect(dropped).toHaveLength(1);
    const item = createPreviewItem(dropped[0]);
    expect(item.logicalPath).toBe("templates/a.txt");
    expect(item.kind).toBe("Template");
  });

  it("leaves individually chosen files on their own names", () => {
    const file = new File(["policy"], "policy.md", { type: "text/markdown" });
    const item = createPreviewItem(file);
    expect(item.logicalPath).toBe("policy.md");
    expect(item.kind).toBe("");
  });
});

describe("common resource formats", () => {
  it.each([
    ["table.csv", "text/csv"], ["table.tsv", "text/tab-separated-values"],
    ["records.jsonl", "application/x-ndjson"], ["config.yaml", "application/yaml"],
    ["notes.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"],
    ["sheet.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"],
    ["sound.mp3", "audio/mpeg"], ["clip.mp4", "video/mp4"]
  ])("recognizes %s without browser MIME metadata", (name, media) => {
    const file = new File(["data"], name);
    expect(resourceMediaType(file, name)).toBe(media);
  });
  it("accepts CSV Knowledge even with the browser Excel MIME alias", () => {
    const file = new File(["name,value\nA,1"], "data.csv", { type: "application/vnd.ms-excel" });
    const item = { ...createPreviewItem(file), logicalPath: "knowledge/data.csv", kind: "Knowledge" };
    expect(item.mediaType).toBe("text/csv");
    expect(resourcePreviewProblem(item, [item.logicalPath], [])).toBeNull();
  });
  it.each(["payload.exe", "script.js", "page.html", "image.svg", "macro.xlsm"])("rejects %s despite an allowed claimed MIME or renamed target", name => {
    const file = new File(["payload"], name, { type: "text/plain" });
    expect(resourceMediaType(file, "safe.txt")).toBeNull();
    expect(resourceMediaType(new File(["data"], "safe.txt", { type: "text/plain" }), name)).toBeNull();
  });
  it("keeps binary office files out of Knowledge", () => {
    const item = { ...createPreviewItem(new File(["data"], "sheet.xlsx")), kind: "Knowledge" };
    expect(resourcePreviewProblem(item, [item.logicalPath], [])).toMatch(/Knowledge requires text/);
  });
  it("accepts extensionless content with a supported MIME without mistaking its name for an extension", () => {
    expect(resourceMediaType(new File(["text"], "exe", { type: "text/plain" }), "exe")).toBe("text/plain");
    expect(resourceMediaType(new File(["<root />"], "payload", { type: "text/xml" }), "data/payload")).toBe("text/xml");
    expect(isTextualResource("TEXT/CSV")).toBe(true);
    expect(resourceMediaType(new File(["text"], "notes.txt", { type: "text/plain" }), "payload.exe ")).toBeNull();
  });
});
