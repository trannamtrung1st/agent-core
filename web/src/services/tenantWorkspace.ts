import { identityFileMutation, identityRequest, tenantApi } from './identity';
import type { AgentWorkspacePage } from './agentWorkspace';

export type TenantWorkspaceOwner = { tenantId: string } & ({ kind: 'home'; spaceId: string } | { kind: 'working'; sessionId: string });
export type WorkspaceReference = { tenantId: string } & ({ kind: 'spaceHome'; spaceId: string; privateAlias?: boolean } | { kind: 'sessionWorking'; sessionId: string });
export type TenantWorkspaceAccess = {
  workspace: WorkspaceReference; securityRevision: number; ownerRevision: number;
  canRead: boolean; canWrite: boolean; canDelete: boolean; readOnly: boolean;
  workspaceName: string; sessionTitle: string | null;
};
export const workspaceRoot = (owner: TenantWorkspaceOwner) => owner.kind === 'home' ? '/home' : '/working';
export const tenantWorkspaceApi = (owner: TenantWorkspaceOwner, suffix = '') => tenantApi(owner.tenantId,
  `${owner.kind === 'home' ? `/spaces/${encodeURIComponent(owner.spaceId)}` : `/sessions/${encodeURIComponent(owner.sessionId)}`}/workspace${suffix}`);
export function sameWorkspace(owner: TenantWorkspaceOwner, reference: WorkspaceReference | null | undefined) {
  return reference?.tenantId === owner.tenantId && (owner.kind === 'home'
    ? reference.kind === 'spaceHome' && reference.spaceId === owner.spaceId && reference.privateAlias !== true
    : reference.kind === 'sessionWorking' && reference.sessionId === owner.sessionId);
}
export async function readWorkspaceAccess(owner: TenantWorkspaceOwner, signal: AbortSignal): Promise<TenantWorkspaceAccess> {
  const value = await identityRequest<TenantWorkspaceAccess>(tenantWorkspaceApi(owner, '/access'), { signal });
  if (!sameWorkspace(owner, value.workspace) || !Number.isSafeInteger(value.securityRevision) || value.securityRevision < 1
    || !Number.isSafeInteger(value.ownerRevision) || value.ownerRevision < 1 || typeof value.workspaceName !== 'string'
    || value.sessionTitle !== null && typeof value.sessionTitle !== 'string'
    || ![value.canRead, value.canWrite, value.canDelete, value.readOnly].every(flag => typeof flag === 'boolean'))
    throw new Error('Current workspace access is unavailable. Reload the workspace.');
  return value;
}
export const listTenantWorkspace = (owner: TenantWorkspaceOwner, prefix: string, signal: AbortSignal, cursor?: string) =>
  identityRequest<AgentWorkspacePage>(tenantWorkspaceApi(owner) + '?' + new URLSearchParams({ prefix, ...(cursor ? { cursor } : {}) }), { signal });
export const mutateTenantWorkspace = (owner: TenantWorkspaceOwner, path: string, expectedRevision: number, signal: AbortSignal, file?: Blob) =>
  identityFileMutation(tenantWorkspaceApi(owner, '/content') + '?' + new URLSearchParams({ path, expectedRevision: String(expectedRevision) }), file ? 'PUT' : 'DELETE', file, signal);
