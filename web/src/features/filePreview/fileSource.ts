import { identityReadResponse, tenantApi } from '../../services/identity';
import { tenantAgentApi, type TenantResourceDetail } from '../../services/tenantAgentConfiguration';
import { tenantWorkspaceApi, type TenantWorkspaceOwner } from '../../services/tenantWorkspace';

export type FileMetadata = {
  displayName: string;
  contentType: string;
  byteSize: number;
  sha256?: string;
};
export type FileRef =
  | { kind: 'tenantWorkspace'; owner: TenantWorkspaceOwner; instanceId: string; path: string; metadata: FileMetadata }
  | { kind: 'tenantResource'; tenantId: string; instanceId: string; resourceKey: string; metadata: FileMetadata }
  | { kind: 'attachment'; tenantId: string; sessionId: string; attachmentId: string }
  | { kind: 'artifact'; tenantId: string; sessionId: string; artifactId: string };
export const sourceLabel = { tenantWorkspace: 'Workspace', tenantResource: 'Instance Resource', attachment: 'Attachment', artifact: 'Artifact' };
export function fileTenantId(ref: FileRef): string { return ref.kind === 'tenantWorkspace' ? ref.owner.tenantId : ref.tenantId; }
const enc = encodeURIComponent;
export function sourcePath(ref: FileRef): string {
  switch (ref.kind) {
    case 'tenantWorkspace':
      return tenantWorkspaceApi(ref.owner);
    case 'tenantResource':
      return tenantAgentApi(ref.tenantId, ref.instanceId, `/resources/${enc(ref.resourceKey)}`);
    case 'attachment':
      return tenantApi(ref.tenantId, `/sessions/${enc(ref.sessionId)}/attachments/${enc(ref.attachmentId)}`);
    case 'artifact':
      return tenantApi(ref.tenantId, `/sessions/${enc(ref.sessionId)}/artifacts/${enc(ref.artifactId)}`);
  }
}
export class PreviewError extends Error {
  constructor(
    public code: string,
    message: string,
  ) {
    super(message);
  }
}
export function readFailure(status: number): PreviewError {
  if (status === 401) return new PreviewError('preview_forbidden', 'Your session has expired. Sign in again to read this file.');
  if (status === 403)
    return new PreviewError(
      'preview_forbidden',
      'You no longer have permission to read this file. Close the preview and check access.',
    );
  if (status === 404)
    return new PreviewError(
      'preview_unavailable',
      'File unavailable. Reload the source list or retry.',
    );
  if (status === 409 || status === 412)
    return new PreviewError(
      'preview_stale',
      'The file changed. Close the preview and reload the source list.',
    );
  return new PreviewError(
    'preview_failed',
    'The file could not be loaded. Retry when the connection is available.',
  );
}
export async function resolveFile(
  ref: FileRef,
  signal: AbortSignal,
): Promise<FileMetadata> {
  if (ref.kind === 'tenantResource') {
    const response = await fileReadResponse(`${sourcePath(ref)}/editor`, signal);
    if (!response.ok) throw readFailure(response.status);
    const value = await response.json() as TenantResourceDetail;
    const item = value?.item?.resource;
    if (value?.agent?.instance?.instanceId !== ref.instanceId || item?.key !== ref.resourceKey || typeof item.logicalPath !== 'string'
      || typeof item.mediaType !== 'string' || !Number.isSafeInteger(item.byteLength) || item.byteLength < 0 || !/^[a-f0-9]{64}$/i.test(item.contentSha256))
      throw new PreviewError('preview_unavailable', 'Current resource metadata is unavailable. Reload the resource list.');
    return { displayName: item.logicalPath.split('/').at(-1)!, contentType: item.mediaType, byteSize: item.byteLength, sha256: item.contentSha256 };
  }
  if (ref.kind === 'tenantWorkspace') {
    const response = await fileReadResponse(`${sourcePath(ref)}/metadata?${new URLSearchParams({path: ref.path})}`, signal);
    if (!response.ok) throw readFailure(response.status);
    const item = await response.json();
    if (item.agentInstanceId !== ref.instanceId || item.logicalPath !== ref.path || typeof item.contentType !== 'string' || !Number.isSafeInteger(item.byteSize) || item.byteSize < 0)
      throw new PreviewError('preview_unavailable', 'Current file metadata is unavailable. Reload the workspace.');
    return {displayName: ref.path.split('/').pop()!, contentType: item.contentType, byteSize: item.byteSize};
  }
  const response = await fileReadResponse(sourcePath(ref), signal);
  if (!response.ok) throw readFailure(response.status);
  const raw = await response.json();
  if (
    raw.sessionId !== ref.sessionId ||
    (ref.kind === 'attachment'
      ? raw.attachmentId !== ref.attachmentId
      : raw.artifactId !== ref.artifactId) ||
    typeof raw.displayName !== 'string' ||
    typeof raw.contentType !== 'string' ||
    !Number.isSafeInteger(raw.byteSize) ||
    raw.byteSize < 0
  )
    throw new PreviewError(
      'preview_unavailable',
      'File metadata is unavailable. Reload the source list.',
    );
  return {
    displayName: raw.displayName,
    contentType: raw.contentType,
    byteSize: raw.byteSize,
    sha256: raw.sha256 ?? raw.sha256Hex,
  };
}
// Bound the stream itself, including chunked responses with absent or incorrect Content-Length.
export async function readFile(
  ref: FileRef,
  metadata: FileMetadata,
  maxBytes: number,
  signal: AbortSignal,
): Promise<Blob> {
  if (ref.kind !== 'tenantWorkspace' && metadata.byteSize > maxBytes)
    throw new PreviewError(
      'preview_too_large',
      `This file is ${(metadata.byteSize / 1048576).toLocaleString(undefined, { maximumFractionDigits: 2 })} MiB; its preview limit is ${(maxBytes / 1048576).toLocaleString(undefined, { maximumFractionDigits: 2 })} MiB. Download the original.`,
    );
  const response = await fileReadResponse(
    `${sourcePath(ref)}/content${ref.kind === 'tenantWorkspace' ? '?' + new URLSearchParams({path: ref.path}) : ''}`,
    signal,
  );
  if (!response.ok) throw readFailure(response.status);
  if (Number(response.headers.get('content-length')) > maxBytes) {
    await response.body?.cancel();
    throw new PreviewError(
      'preview_too_large',
      `The received file exceeds its ${(maxBytes / 1048576).toLocaleString(undefined, { maximumFractionDigits: 2 })} MiB preview limit. Download the original.`,
    );
  }
  const reader = response.body?.getReader();
  const chunks: ArrayBuffer[] = [];
  let size = 0;
  try {
    if (!reader)
      throw new PreviewError(
        'preview_failed',
        'The file could not be read. Retry.',
      );
    while (true) {
      signal.throwIfAborted();
      const { value, done } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > maxBytes)
        throw new PreviewError(
          'preview_too_large',
          `The received file exceeds its ${(maxBytes / 1048576).toLocaleString(undefined, { maximumFractionDigits: 2 })} MiB preview limit. Download the original.`,
        );
      chunks.push(value.slice().buffer as ArrayBuffer);
    }
  } finally {
    await reader?.cancel().catch(() => undefined);
    reader?.releaseLock();
  }
  signal.throwIfAborted();
  const blob = new Blob(chunks, { type: metadata.contentType });
  if (ref.kind !== 'tenantWorkspace' && blob.size !== metadata.byteSize) throw readFailure(409);
  if (ref.kind !== 'tenantWorkspace' && metadata.sha256) {
    const hash = await crypto.subtle.digest(
      'SHA-256',
      await blob.arrayBuffer(),
    );
    const hex = Array.from(new Uint8Array(hash), (x) =>
      x.toString(16).padStart(2, '0'),
    ).join('');
    if (hex !== metadata.sha256.toLowerCase()) throw readFailure(409);
  }
  return blob;
}
export function fileReadResponse(path: string, signal: AbortSignal): Promise<Response> {
  return identityReadResponse(path, signal);
}
export const fileReadLost = (error: unknown) => error instanceof PreviewError && ['preview_forbidden', 'preview_unavailable'].includes(error.code);
export async function downloadFile(
  ref: FileRef,
  signal: AbortSignal,
): Promise<void> {
  const metadata = await resolveFile(ref, signal);
  const blob = await readFile(ref, metadata, ref.kind === 'tenantWorkspace' ? 50 * 1024 * 1024 : metadata.byteSize, signal);
  signal.throwIfAborted();
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  try {
    link.href = url;
    link.download = metadata.displayName;
    link.hidden = true;
    document.body.append(link);
    link.click();
  } finally {
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
}
