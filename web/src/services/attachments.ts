import { ensureOwnerCapability, ownerFetch } from "./api";

export type AttachmentDto = {
  attachmentId: string;
  sessionId: string;
  displayName: string;
  contentType: string;
  byteSize: number;
  sha256: string;
  state: string;
  readable: boolean;
  entryId: string | null;
  createdAt: string;
  expiresAt: string | null;
};

export type PendingAttachment = {
  localId: string;
  displayName: string;
  contentType: string;
  byteSize: number;
  status: "uploading" | "ready" | "error";
  progress: number;
  attachmentId: string | null;
  error: string | null;
};

const files = new Map<string, File>();
const uploads = new Map<string, XMLHttpRequest>();
const previews = new Map<string, string>();

export function pendingPreviewUrl(localId: string): string | null {
  return previews.get(localId) ?? null;
}

export function retainPendingFile(localId: string, file: File): void {
  files.set(localId, file);
  if (file.type.startsWith("image/")) {
    const existing = previews.get(localId);
    if (existing) {
      URL.revokeObjectURL(existing);
    }
    previews.set(localId, URL.createObjectURL(file));
  }
}

export function releasePendingFile(localId: string): void {
  files.delete(localId);
  uploads.get(localId)?.abort();
  uploads.delete(localId);
  const preview = previews.get(localId);
  if (preview) {
    URL.revokeObjectURL(preview);
    previews.delete(localId);
  }
}

export function releaseAllPendingFiles(): void {
  for (const id of [...files.keys()]) {
    releasePendingFile(id);
  }
}

export function pendingFile(localId: string): File | undefined {
  return files.get(localId);
}

export async function listAttachments(sessionId: string): Promise<AttachmentDto[]> {
  const response = await ownerFetch(`/api/v2/sessions/${sessionId}/attachments`);
  if (!response.ok) {
    throw new Error("Unable to load attachments.");
  }

  return (await response.json()) as AttachmentDto[];
}

export async function stageAttachments(sessionId: string, attachmentIds: string[]): Promise<void> {
  if (attachmentIds.length === 0) {
    return;
  }

  const response = await ownerFetch(`/api/v2/sessions/${sessionId}/attachments/stage`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ attachmentIds })
  });
  if (!response.ok) {
    throw new Error("Unable to stage attachments.");
  }
}

export async function abortPendingAttachment(sessionId: string, attachmentId: string): Promise<void> {
  await ownerFetch(`/api/v2/sessions/${sessionId}/attachments/${attachmentId}`, { method: "DELETE" });
}

export async function fetchAttachmentBlob(sessionId: string, attachmentId: string): Promise<Blob> {
  const response = await ownerFetch(`/api/v2/sessions/${sessionId}/attachments/${attachmentId}/content`);
  if (!response.ok) {
    throw new Error("Unable to load attachment.");
  }

  return response.blob();
}

export function uploadAttachment(
  sessionId: string,
  localId: string,
  file: File,
  onProgress: (percent: number) => void
): Promise<AttachmentDto> {
  return new Promise((resolve, reject) => {
    void (async () => {
      try {
        const token = await ensureOwnerCapability();
        const body = new FormData();
        body.append("file", file, file.name);
        const xhr = new XMLHttpRequest();
        uploads.set(localId, xhr);
        xhr.open("POST", `/api/v2/sessions/${sessionId}/attachments`);
        xhr.setRequestHeader("X-AgentCore-Owner-Capability", token);
        xhr.upload.onprogress = (event) => {
          if (event.lengthComputable) {
            onProgress(Math.round((event.loaded / event.total) * 100));
          }
        };
        xhr.onload = () => {
          uploads.delete(localId);
          if (xhr.status === 201) {
            resolve(JSON.parse(xhr.responseText) as AttachmentDto);
            return;
          }

          reject(new Error(readProblem(xhr.responseText) || "Upload failed."));
        };
        xhr.onerror = () => {
          uploads.delete(localId);
          reject(new Error("Upload failed."));
        };
        xhr.onabort = () => {
          uploads.delete(localId);
          reject(new DOMException("Aborted", "AbortError"));
        };
        xhr.send(body);
      } catch (error) {
        reject(error);
      }
    })();
  });
}

function readProblem(raw: string): string | null {
  try {
    const body = JSON.parse(raw) as { detail?: string };
    return body.detail ?? null;
  } catch {
    return null;
  }
}
