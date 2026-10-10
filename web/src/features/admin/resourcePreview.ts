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
  csv: "text/csv",
  tsv: "text/tab-separated-values",
  jsonl: "application/x-ndjson",
  ndjson: "application/x-ndjson",
  yaml: "application/yaml",
  yml: "application/yaml",
  toml: "application/toml",
  xml: "application/xml",
  log: "text/plain",
  ini: "text/plain",
  rtf: "application/rtf",
  doc: "application/msword",
  docx: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
  xls: "application/vnd.ms-excel",
  xlsx: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
  ppt: "application/vnd.ms-powerpoint",
  pptx: "application/vnd.openxmlformats-officedocument.presentationml.presentation",
  odt: "application/vnd.oasis.opendocument.text",
  ods: "application/vnd.oasis.opendocument.spreadsheet",
  odp: "application/vnd.oasis.opendocument.presentation",
  gif: "image/gif",
  bmp: "image/bmp",
  tif: "image/tiff",
  tiff: "image/tiff",
  avif: "image/avif",
  mp3: "audio/mpeg",
  wav: "audio/wav",
  ogg: "audio/ogg",
  flac: "audio/flac",
  m4a: "audio/mp4",
  mp4: "video/mp4",
  webm: "video/webm",
  mov: "video/quicktime",
  png: "image/png",
  jpg: "image/jpeg",
  jpeg: "image/jpeg",
  webp: "image/webp",
  pdf: "application/pdf"
};

const BLOCKED_EXTENSIONS = new Set("exe dll com scr msi msp bat cmd ps1 sh bash zsh js mjs cjs vbs vbe wsf wsh jar app dmg pkg deb rpm html htm xhtml svg hta lnk url docm dotm xlsm xltm xlam pptm potm ppam".split(" "));
export const RESOURCE_FILE_HELP = "Supports text, CSV/TSV, JSON, YAML, XML, Office/OpenDocument, PDF, images, audio and video. Executable and active-content files are blocked. Up to 8 MiB per file.";
export function isTextualResource(mediaType: string): boolean {
  return ["text/plain", "text/markdown", "text/csv", "text/tab-separated-values", "application/json", "application/x-ndjson", "application/yaml", "application/toml", "application/xml", "text/xml"].includes(mediaType.toLowerCase());
}
function blockedPath(path: string): boolean {
  const name = path.trim().split(/[\\/]/).at(-1) ?? "";
  const dot = name.lastIndexOf(".");
  return dot >= 0 && BLOCKED_EXTENSIONS.has(name.slice(dot + 1).toLowerCase());
}

const ALLOWED_MEDIA = new Set([...Object.values(EXTENSION_MEDIA), "text/xml"]);

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
  if (segments.length < 2) {
    return null;
  }
  return FOLDER_KINDS[segments[0].toLowerCase()] ?? null;
}

export function packageLogicalPath(relativePath: string): string {
  const normalized = relativePath.replaceAll("\\", "/").replace(/^\/+/, "");
  const segments = normalized.split("/").filter((segment) => segment.length > 0);
  if (segments.length <= 1) {
    return segments[0] ?? "";
  }
  return segments.slice(1).join("/");
}

export function resourceRelativePath(file: File): string {
  const relative = (file as File & { webkitRelativePath?: string }).webkitRelativePath?.trim() ?? "";
  if (relative.length > 0) {
    return packageLogicalPath(relative);
  }
  return file.name;
}

export function resourceMediaType(file: File, logicalPath: string): string | null {
  if (blockedPath(logicalPath) || blockedPath(file.name)) return null;
  return extensionMedia(file.name) ?? extensionMedia(logicalPath) ?? allowedFileType(file.type);
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
  existingPaths: string[],
  scope = "draft"
): string | null {
  if (item.uploadError) {
    return item.uploadError;
  }
  return resourcePathProblem(item.logicalPath, siblingPaths, existingPaths, scope)
    ?? resourceFileProblem(item)
    ?? resourceKindProblem(item);
}

export function resourcePathProblem(logicalPath: string, siblingPaths: string[], existingPaths: string[], scope = "draft"): string | null {
  const path = logicalPath.trim();
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
    return `Path is already bound on this ${scope}.`;
  }
  return null;
}

export function resourceFileProblem(item: Pick<ResourcePreviewItem, "logicalPath" | "byteLength" | "mediaType">): string | null {
  if (item.byteLength <= 0) {
    return "File is empty.";
  }
  if (item.byteLength > MAX_RESOURCE_ITEM_BYTES) {
    return "File is larger than 8 MiB.";
  }
  if (blockedPath(item.logicalPath.trim())) return "Executable and active-content files are not supported.";
  if (!item.mediaType) {
    return "This file type is not supported.";
  }
  return null;
}

export function resourceKindProblem(item: Pick<ResourcePreviewItem, "kind" | "mediaType">): string | null {
  if (!RESOURCE_KINDS.includes(item.kind as ResourceKindName)) {
    return "Choose a kind.";
  }
  if (item.kind === "Knowledge" && item.mediaType && !isTextualResource(item.mediaType)) {
    return "Knowledge requires text. Choose Reference or Static asset for this file.";
  }
  return null;
}

export function resourceBatchLimitProblem(
  items: Array<Pick<ResourcePreviewItem, "byteLength">>,
  existingCount: number,
  existingBytes: number,
  scope = "draft"
): string | null {
  if (existingCount + items.length > MAX_RESOURCE_ITEMS) {
    return `This import would exceed 64 resources on the ${scope}.`;
  }
  const incomingBytes = items.reduce((sum, item) => sum + item.byteLength, 0);
  if (existingBytes + incomingBytes > MAX_RESOURCE_AGGREGATE_BYTES) {
    return `This import would exceed 64 MiB on the ${scope}.`;
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
