import { identityRequest, identityUpload, tenantApi } from './identity';
import { chatTenantId, tenantTextWitness } from './tenantChat';

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
  originalText?: string;
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
function tenantPath(session: string, suffix = '') { if (!chatTenantId) throw new Error('Choose an organization before attaching files.'); return tenantApi(chatTenantId, `/sessions/${encodeURIComponent(session)}/attachments${suffix}`); }
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

export async function abortPendingAttachment(sessionId: string, attachmentId: string): Promise<void> {
  return tenantWrite(async () => {
    const witness = await tenantTextWitness(sessionId);
    const query = new URLSearchParams({ requestId: crypto.randomUUID(), expectedRevision: String(witness.expectedRevision), expectedSessionRevision: String(witness.expectedSessionRevision) });
    await identityRequest(tenantPath(sessionId, `/${encodeURIComponent(attachmentId)}?${query}`), { method: 'DELETE' });
  });
}

export function uploadAttachment(
  sessionId: string,
  localId: string,
  file: File,
  onProgress: (percent: number) => void
): Promise<UploadedAttachment> {
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
