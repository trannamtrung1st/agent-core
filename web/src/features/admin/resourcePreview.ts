export const RESOURCE_KINDS = ["Knowledge", "Reference", "Template", "StaticAsset", "EvalFixture"] as const;

export type ResourceKindName = (typeof RESOURCE_KINDS)[number];

export const MAX_RESOURCE_ITEM_BYTES = 8 * 1024 * 1024;
export const MAX_RESOURCE_ITEMS = 64;
export const MAX_RESOURCE_AGGREGATE_BYTES = 64 * 1024 * 1024;

const FOLDER_KINDS: Record<string, ResourceKindName> = {
  knowledge: "Knowledge",
  templates: "Template",
  references: "Reference",
  assets: "StaticAsset",
  eval: "EvalFixture"
};

const EXTENSION_MEDIA: Record<string, string> = {
  md: "text/markdown",
  markdown: "text/markdown",
  txt: "text/plain",
  json: "application/json",
  png: "image/png",
  jpg: "image/jpeg",
  jpeg: "image/jpeg",
  webp: "image/webp",
  pdf: "application/pdf"
};

const ALLOWED_MEDIA = new Set(Object.values(EXTENSION_MEDIA));

export type ResourcePreviewItem = {
  key: string;
  file: File;
  logicalPath: string;
  kind: string;
  byteLength: number;
  mediaType: string | null;
  contentSha256?: string;
  uploadError?: string;
};

type DirectoryEntry = {
  isFile: boolean;
  isDirectory: boolean;
  name: string;
  fullPath?: string;
  file?: (success: (file: File) => void, error?: (reason: DOMException) => void) => void;
  createReader?: () => {
    readEntries: (success: (entries: DirectoryEntry[]) => void, error?: (reason: DOMException) => void) => void;
  };
};

export function inferResourceKind(logicalPath: string): ResourceKindName | null {
  const segments = logicalPath.split("/").filter((segment) => segment.length > 0);
  const folders = segments.slice(0, -1);
  for (const folder of folders) {
    const kind = FOLDER_KINDS[folder.toLowerCase()];
    if (kind) {
      return kind;
    }
  }
  return null;
}

export function resourceRelativePath(file: File): string {
  const relative = (file as File & { webkitRelativePath?: string }).webkitRelativePath?.trim() ?? "";
  if (relative.length > 0) {
    return relative.replaceAll("\\", "/").replace(/^\/+/, "");
  }
  return file.name;
}

export function resourceMediaType(file: File, logicalPath: string): string | null {
  return extensionMedia(logicalPath) ?? extensionMedia(file.name) ?? allowedFileType(file.type);
}

export function createPreviewItem(file: File): ResourcePreviewItem {
  const logicalPath = resourceRelativePath(file);
  return {
    key: `${logicalPath}:${file.size}:${file.lastModified}:${crypto.randomUUID()}`,
    file,
    logicalPath,
    kind: inferResourceKind(logicalPath) ?? "",
    byteLength: file.size,
    mediaType: resourceMediaType(file, logicalPath)
  };
}

export function resourcePreviewProblem(
  item: Pick<ResourcePreviewItem, "logicalPath" | "kind" | "byteLength" | "mediaType" | "uploadError">,
  siblingPaths: string[],
  existingPaths: string[]
): string | null {
  if (item.uploadError) {
    return item.uploadError;
  }
  const path = item.logicalPath.trim();
  if (path.length === 0) {
    return "Path is required.";
  }
  if (path.startsWith("/") || path.includes("\\") || path.includes(":")) {
    return "Path must be a relative path without a drive or leading slash.";
  }
  const segments = path.split("/");
  if (segments.some((segment) => segment.length === 0 || segment === "." || segment === "..")) {
    return "Path must not contain traversal segments.";
  }
  if (path.length > 240) {
    return "Path is too long.";
  }
  if (siblingPaths.filter((candidate) => candidate.trim() === path).length > 1) {
    return "Path is duplicated in this import.";
  }
  if (existingPaths.includes(path)) {
    return "Path is already bound on this draft.";
  }
  if (item.byteLength <= 0) {
    return "File is empty.";
  }
  if (item.byteLength > MAX_RESOURCE_ITEM_BYTES) {
    return "File is larger than 8 MiB.";
  }
  if (!item.mediaType) {
    return "This file type is not supported.";
  }
  if (!RESOURCE_KINDS.includes(item.kind as ResourceKindName)) {
    return "Choose a kind.";
  }
  return null;
}

export function resourceBatchLimitProblem(
  items: Array<Pick<ResourcePreviewItem, "byteLength">>,
  existingCount: number,
  existingBytes: number
): string | null {
  if (existingCount + items.length > MAX_RESOURCE_ITEMS) {
    return "This import would exceed 64 resources on the draft.";
  }
  const incomingBytes = items.reduce((sum, item) => sum + item.byteLength, 0);
  if (existingBytes + incomingBytes > MAX_RESOURCE_AGGREGATE_BYTES) {
    return "This import would exceed 64 MiB on the draft.";
  }
  return null;
}

export async function readDroppedResourceFiles(data: DataTransfer): Promise<File[]> {
  const entries = Array.from(data.items ?? [])
    .map((item) => {
      const read = (item as DataTransferItem & { webkitGetAsEntry?: () => DirectoryEntry | null }).webkitGetAsEntry;
      return read?.call(item) ?? null;
    })
    .filter((entry): entry is DirectoryEntry => entry !== null);
  if (entries.length === 0) {
    return Array.from(data.files ?? []);
  }

  const files: File[] = [];
  for (const entry of entries) {
    await collectEntry(entry, files);
  }
  return files;
}

function extensionMedia(path: string): string | null {
  const name = path.split("/").pop() ?? "";
  const dot = name.lastIndexOf(".");
  if (dot < 0) {
    return null;
  }
  return EXTENSION_MEDIA[name.slice(dot + 1).toLowerCase()] ?? null;
}

function allowedFileType(type: string): string | null {
  const normalized = type.trim().toLowerCase();
  return ALLOWED_MEDIA.has(normalized) ? normalized : null;
}

async function collectEntry(entry: DirectoryEntry, files: File[]): Promise<void> {
  if (entry.isFile) {
    const file = await fileFromEntry(entry);
    const relative = (entry.fullPath ?? file.name).replaceAll("\\", "/").replace(/^\/+/, "");
    Object.defineProperty(file, "webkitRelativePath", { value: relative, configurable: true });
    files.push(file);
    return;
  }

  if (!entry.isDirectory || !entry.createReader) {
    return;
  }

  const reader = entry.createReader();
  const children: DirectoryEntry[] = [];
  while (true) {
    const page = await readEntries(reader);
    if (page.length === 0) {
      break;
    }
    children.push(...page);
  }
  for (const child of children) {
    await collectEntry(child, files);
  }
}

function fileFromEntry(entry: DirectoryEntry): Promise<File> {
  return new Promise((resolve, reject) => {
    if (!entry.file) {
      reject(new Error("Dropped folder entry did not include a file."));
      return;
    }
    entry.file(resolve, reject);
  });
}

function readEntries(reader: {
  readEntries: (success: (entries: DirectoryEntry[]) => void, error?: (reason: DOMException) => void) => void;
}): Promise<DirectoryEntry[]> {
  return new Promise((resolve, reject) => {
    reader.readEntries(resolve, reject);
  });
}
