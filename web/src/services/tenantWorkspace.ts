import { identityFileMutation, identityRequest, tenantApi } from './identity';
import type { AgentWorkspacePage } from './agentWorkspace';

export type TenantWorkspaceOwner = { tenantId: string } & ({ kind: 'home'; instanceId: string } | { kind: 'working'; sessionId: string });
export type TenantWorkspaceAccess = {
  workspace: { tenantId: string; instanceId: string; sessionId: string | null; logicalRoot: string };
  securityRevision: number; instanceRevision: number | null; canRead: boolean; canWrite: boolean; canDelete: boolean; readOnly: boolean;
  instanceName: string | null; sessionTitle: string | null;
};
export const workspaceRoot = (owner: TenantWorkspaceOwner) => owner.kind === 'home' ? '/home' : '/working';
export const tenantWorkspaceApi = (owner: TenantWorkspaceOwner, suffix = '') => tenantApi(owner.tenantId,
  `${owner.kind === 'home' ? `/agents/${encodeURIComponent(owner.instanceId)}` : `/sessions/${encodeURIComponent(owner.sessionId)}`}/workspace${suffix}`);
export async function readWorkspaceAccess(owner: TenantWorkspaceOwner, signal: AbortSignal): Promise<TenantWorkspaceAccess> {
  const value = await identityRequest<TenantWorkspaceAccess>(tenantWorkspaceApi(owner, '/access'), { signal });
  if (value.workspace?.tenantId !== owner.tenantId || value.workspace.logicalRoot !== workspaceRoot(owner)
    || (owner.kind === 'home' ? value.workspace.instanceId !== owner.instanceId || value.workspace.sessionId !== null : value.workspace.sessionId !== owner.sessionId)
    || !Number.isSafeInteger(value.securityRevision) || value.securityRevision < 1
    || (owner.kind === 'home' ? !Number.isSafeInteger(value.instanceRevision) || value.instanceRevision === null || value.instanceRevision < 1 || typeof value.instanceName !== 'string'
      : value.instanceRevision !== null || value.instanceName !== null)
    || typeof value.workspace.instanceId !== 'string' || !value.workspace.instanceId || value.sessionTitle !== null && typeof value.sessionTitle !== 'string'
    || ![value.canRead, value.canWrite, value.canDelete, value.readOnly].every(flag => typeof flag === 'boolean'))
    throw new Error('Current workspace access is unavailable. Reload the workspace.');
  return value;
}
export const listTenantWorkspace = (owner: TenantWorkspaceOwner, prefix: string, signal: AbortSignal, cursor?: string) =>
  identityRequest<AgentWorkspacePage>(tenantWorkspaceApi(owner) + '?' + new URLSearchParams({ prefix, ...(cursor ? { cursor } : {}) }), { signal });
export const mutateTenantWorkspace = (owner: TenantWorkspaceOwner, path: string, expectedRevision: number, signal: AbortSignal, file?: Blob) =>
  identityFileMutation(tenantWorkspaceApi(owner, '/content') + '?' + new URLSearchParams({ path, expectedRevision: String(expectedRevision) }), file ? 'PUT' : 'DELETE', file, signal);
