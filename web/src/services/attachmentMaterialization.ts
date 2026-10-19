import { identityRequest, IdentityCompletionError, tenantApi } from './identity';
export type AttachmentCopyAccess = { displayName: string; securityRevision: number; canMaterialize: boolean; readOnly: boolean };
export type AttachmentCopyResult = { artifactId: string; workspaceLogicalPath: string };
const path = (tenant: string, session: string, source: string) => tenantApi(tenant, `/sessions/${encodeURIComponent(session)}/attachments/${encodeURIComponent(source)}/materialize`);
export async function readAttachmentCopyAccess(tenant: string, session: string, source: string, signal: AbortSignal): Promise<AttachmentCopyAccess> {
  const value = await identityRequest<AttachmentCopyAccess>(path(tenant, session, source), { signal });
  if (typeof value.displayName !== 'string' || !value.displayName || !Number.isSafeInteger(value.securityRevision) || value.securityRevision < 1
    || typeof value.canMaterialize !== 'boolean' || typeof value.readOnly !== 'boolean') throw new Error('Current file access could not be loaded.');
  return value;
}
export async function copyAttachment(tenant: string, session: string, source: string, expectedRevision: number, signal: AbortSignal): Promise<AttachmentCopyResult> {
  const value = await identityRequest<AttachmentCopyResult>(path(tenant, session, source), { method: 'POST', body: { expectedRevision }, signal });
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value.artifactId)
    || typeof value.workspaceLogicalPath !== 'string' || !/^\/working\/[a-zA-Z0-9_.-]+$/.test(value.workspaceLogicalPath)) throw new IdentityCompletionError();
  return value;
}
