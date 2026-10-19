import { ownerFetch, type CursorPage } from './api';
import { identityReadResponse, identityRequest, IdentityError, tenantApi } from './identity';

// Immutable metadata only. Pending requests share the same bound as completed reads.
const metadata = new Map<string, Promise<ArtifactMetadata>>();
const metadataLimit = 128;

export type ArtifactMetadata = {
  artifactId: string;
  sessionId: string;
  displayName: string;
  contentType: string;
  byteSize: number;
};

function artifactPath(sessionId: string, artifactId: string, tenantId?: string): string {
  const suffix = `/sessions/${encodeURIComponent(sessionId)}/artifacts/${encodeURIComponent(artifactId)}`;
  return tenantId ? tenantApi(tenantId, suffix) : `/api/v2${suffix}`;
}

export async function listArtifactPage(
  sessionId: string,
  before?: string,
  agentRunId?: string,
  tenantId?: string,
  signal?: AbortSignal,
): Promise<CursorPage<ArtifactMetadata>> {
  const query = new URLSearchParams({
    limit: '20',
    ...(before ? { before } : {}),
    ...(agentRunId ? { agentRunId } : {}),
  });
  let page: CursorPage<ArtifactMetadata>;
  if (tenantId) page = await identityRequest(tenantApi(tenantId, `/sessions/${encodeURIComponent(sessionId)}/artifacts?${query}`), { signal });
  else {
    const response = await ownerFetch(`/api/v2/sessions/${encodeURIComponent(sessionId)}/artifacts/page?${query}`, { signal });
    if (!response.ok) throw new Error('Files could not be loaded. Try again.');
    page = await response.json() as CursorPage<ArtifactMetadata>;
  }
  if (
    !Array.isArray(page.items) ||
    page.items.length > 20 ||
    typeof page.hasMore !== 'boolean' ||
    page.items.some(
      (file) =>
        file.sessionId !== sessionId ||
        typeof file.artifactId !== 'string' ||
        typeof file.displayName !== 'string' ||
        typeof file.contentType !== 'string' ||
        !Number.isSafeInteger(file.byteSize) ||
        file.byteSize < 0,
    )
  )
    throw new Error('Files could not be loaded. Try again.');
  return {
    items: page.items.map((file) => ({
      artifactId: file.artifactId,
      sessionId: file.sessionId,
      displayName: file.displayName,
      contentType: file.contentType,
      byteSize: file.byteSize,
    })),
    nextCursor: page.nextCursor,
    hasMore: page.hasMore,
  };
}

export function getArtifact(
  sessionId: string,
  artifactId: string,
  tenantId?: string,
  signal?: AbortSignal,
): Promise<ArtifactMetadata> {
  const key = JSON.stringify([sessionId, artifactId]);
  const cached = tenantId ? undefined : metadata.get(key);
  if (cached) {
    metadata.delete(key);
    metadata.set(key, cached);
    return cached;
  }
  const request = (async () => {
    const response = tenantId ? await identityReadResponse(artifactPath(sessionId, artifactId, tenantId), signal ?? AbortSignal.timeout(10000))
      : await ownerFetch(artifactPath(sessionId, artifactId), { signal });
    if (!response.ok) { if (tenantId) throw new IdentityError(response.status, 'resource_unavailable'); throw new Error('File unavailable.'); }
    const body = (await response.json()) as ArtifactMetadata;
    if (
      body.sessionId !== sessionId ||
      body.artifactId !== artifactId ||
      typeof body.displayName !== 'string' ||
      !body.displayName.trim() ||
      typeof body.contentType !== 'string' ||
      !Number.isSafeInteger(body.byteSize) ||
      body.byteSize < 0
    ) {
      throw new Error('File unavailable.');
    }
    // Do not retain hashes, provenance or workspace paths from the HTTP DTO.
    return {
      artifactId: body.artifactId,
      sessionId: body.sessionId,
      displayName: body.displayName,
      contentType: body.contentType,
      byteSize: body.byteSize,
    };
  })();
  if (tenantId) return request; // Current recipient metadata is never cached across identity/access changes.
  metadata.set(key, request);
  if (metadata.size > metadataLimit)
    metadata.delete(metadata.keys().next().value!);
  void request.catch(() => {
    if (metadata.get(key) === request) metadata.delete(key);
  });
  return request;
}

export async function downloadArtifact(
  sessionId: string,
  artifactId: string,
  signal?: AbortSignal,
  tenantId?: string,
): Promise<void> {
  const file = await getArtifact(sessionId, artifactId, tenantId, signal);
  signal?.throwIfAborted();
  const response = tenantId ? await identityReadResponse(`${artifactPath(sessionId, artifactId, tenantId)}/content`, signal ?? AbortSignal.timeout(30000))
    : await ownerFetch(`${artifactPath(sessionId, artifactId)}/content`, { signal });
  if (!response.ok) { if (tenantId) throw new IdentityError(response.status, 'resource_unavailable'); throw new Error('Download failed. Try again.'); }
  const blob = await response.blob();
  signal?.throwIfAborted();
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  try {
    link.href = url;
    link.download = file.displayName;
    link.hidden = true;
    document.body.append(link);
    link.click();
  } finally {
    link.remove();
    // Let the browser consume the URL before releasing it. No body enters UI state.
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
}

export function artifactKind(contentType: string): string {
  const mime = contentType.split(';', 1)[0].trim().toLowerCase();
  if (mime.startsWith('image/')) return 'Image';
  return (
    (
      {
        'application/pdf': 'PDF',
        'text/markdown': 'Markdown',
        'text/csv': 'CSV',
        'application/vnd.openxmlformats-officedocument.wordprocessingml.document':
          'Word',
        'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet':
          'Excel',
        'application/json': 'JSON',
        'text/plain': 'Text',
        'application/zip': 'ZIP',
      } as Record<string, string>
    )[mime] ?? 'File'
  );
}

export function artifactSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
