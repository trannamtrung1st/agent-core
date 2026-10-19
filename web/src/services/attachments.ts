import { chatTenantId, tenantTextWitness } from './tenantChat';
import { identityBlob, identityRequest, identityUpload, tenantApi } from './identity';
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
  readable?: boolean;
};

const files = new Map<string, File>();
const uploads = new Map<string, { abort(): void }>();
// Upload/abort each read a fresh security revision after the preceding local mutation settles.
let tenantWrites: Promise<unknown> = Promise.resolve();
function tenantWrite<T>(effect: () => Promise<T>): Promise<T> {
  const next = tenantWrites.catch(() => undefined).then(effect); tenantWrites = next; return next;
}
function tenantPath(session: string, suffix = '') { return tenantApi(chatTenantId!, `/sessions/${encodeURIComponent(session)}/attachments${suffix}`); }
export type UploadedAttachment = Pick<AttachmentDto, 'attachmentId' | 'displayName' | 'contentType'> & { readable?: boolean };

export function retainPendingFile(localId: string, file: File): void {
  files.set(localId, file);
}

export function releasePendingFile(localId: string): void {
  files.delete(localId);
  uploads.get(localId)?.abort();
  uploads.delete(localId);
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
  if (chatTenantId) return (await identityRequest<{ items: AttachmentDto[] }>(tenantPath(sessionId))).items;
  const response = await ownerFetch(`/api/v2/sessions/${sessionId}/attachments`);
  if (!response.ok) {
    throw new Error("Unable to load attachments.");
  }

  return (await response.json()) as AttachmentDto[];
}

export async function stageAttachments(sessionId: string, attachmentIds: string[]): Promise<void> {
  if (chatTenantId) throw new Error('Voice attachment staging is unavailable in this conversation. Send the files as a message.');
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
  if (chatTenantId) return tenantWrite(async () => {
    const witness = await tenantTextWitness(sessionId);
    const query = new URLSearchParams({ requestId: crypto.randomUUID(), expectedRevision: String(witness.expectedRevision), expectedSessionRevision: String(witness.expectedSessionRevision) });
    await identityRequest(tenantPath(sessionId, `/${encodeURIComponent(attachmentId)}?${query}`), { method: 'DELETE' });
  });
  await ownerFetch(`/api/v2/sessions/${sessionId}/attachments/${attachmentId}`, { method: "DELETE" });
}

export async function fetchAttachmentBlob(sessionId: string, attachmentId: string): Promise<Blob> {
  if (chatTenantId) return identityBlob(tenantPath(sessionId, `/${encodeURIComponent(attachmentId)}/content`));
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
): Promise<UploadedAttachment> {
  if (chatTenantId) {
    const stop = new AbortController(); uploads.set(localId, stop);
    return tenantWrite(async () => {
      stop.signal.throwIfAborted(); const witness = await tenantTextWitness(sessionId); stop.signal.throwIfAborted();
      const query = new URLSearchParams({ uploadId: localId, expectedRevision: String(witness.expectedRevision), expectedSessionRevision: String(witness.expectedSessionRevision) });
      const body = new FormData(); body.append('file', file, file.name);
      const saved = await identityUpload<{ attachmentId: string }>(tenantPath(sessionId, `?${query}`), body, stop.signal, onProgress);
      if (typeof saved.attachmentId !== 'string' || !saved.attachmentId) throw new Error('Upload acknowledgement could not be read. Retry this same file.');
      // This is the human's local tile, not a claimed metadata read or canonical content type.
      return { attachmentId: saved.attachmentId, displayName: file.name, contentType: file.type || 'application/octet-stream' };
    }).finally(() => { if (uploads.get(localId) === stop) uploads.delete(localId); });
  }
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
