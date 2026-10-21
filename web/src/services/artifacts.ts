import type { CursorPage } from './api';
import { identityReadResponse, identityRequest, IdentityError, tenantApi } from './identity';

export type ArtifactMetadata = {
  artifactId: string;
  sessionId: string;
  displayName: string;
  contentType: string;
  byteSize: number;
};

function artifactPath(sessionId: string, artifactId: string, tenantId: string): string {
  const suffix = `/sessions/${encodeURIComponent(sessionId)}/artifacts/${encodeURIComponent(artifactId)}`;
  return tenantApi(tenantId, suffix);
}

export async function listArtifactPage(
  sessionId: string,
  before: string | undefined,
  agentRunId: string | undefined,
  tenantId: string,
  signal?: AbortSignal,
): Promise<CursorPage<ArtifactMetadata>> {
  const query = new URLSearchParams({
    limit: '20',
    ...(before ? { before } : {}),
    ...(agentRunId ? { agentRunId } : {}),
  });
  const page = await identityRequest<CursorPage<ArtifactMetadata>>(tenantApi(tenantId, `/sessions/${encodeURIComponent(sessionId)}/artifacts?${query}`), { signal });
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
  tenantId: string,
  signal?: AbortSignal,
): Promise<ArtifactMetadata> {
  const request = (async () => {
    const response = await identityReadResponse(artifactPath(sessionId, artifactId, tenantId), signal ?? AbortSignal.timeout(10000));
    if (!response.ok) throw new IdentityError(response.status, 'resource_unavailable');
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
  return request;
}

export async function downloadArtifact(
  sessionId: string,
  artifactId: string,
  signal: AbortSignal | undefined,
  tenantId: string,
): Promise<void> {
  const file = await getArtifact(sessionId, artifactId, tenantId, signal);
  signal?.throwIfAborted();
  const response = await identityReadResponse(`${artifactPath(sessionId, artifactId, tenantId)}/content`, signal ?? AbortSignal.timeout(30000));
  if (!response.ok) throw new IdentityError(response.status, 'resource_unavailable');
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
