import { IdentityCompletionError, identityRequest, tenantApi } from './identity';
import type { SandboxProfile, SandboxVerification } from './sandboxes';
export type TenantSandboxProfile = { profile: SandboxProfile; canManage: boolean; securityRevision: number };
export type TenantSandboxCatalog = { items: TenantSandboxProfile[]; total: number; page: number; pageSize: number; canCreate: boolean; allReadable: boolean; securityRevision: number };
export type TenantSandboxAttempt = { attemptId: string; profileId: string; profileRevision: number; imageOnly: boolean; status: string; startedAt: string; active: boolean; accepted: boolean; result: SandboxVerification | null };
export type TenantSandboxVerification = { attempt: TenantSandboxAttempt | null; currentProfileRevision: number; securityRevision: number };
export const tenantSandboxApi = (tenant: string, id?: string, suffix = '') => tenantApi(tenant, '/sandbox-profiles' + (id ? '/' + encodeURIComponent(id) : '') + suffix);
export async function saveTenantSandbox(tenant: string, baseline: TenantSandboxProfile | null, revision: number, values: SandboxProfile, signal: AbortSignal) {
  const { name, description, enabled, imageReference, initializationScript, cpu, memoryMiB, pids, maxCommandSeconds, idleTtlSeconds, maxLifetimeSeconds, storageBytes, registryCredentialId } = values;
  try {
    const saved = await identityRequest<TenantSandboxProfile>(tenantSandboxApi(tenant, baseline?.profile.id), { method: baseline ? 'PUT' : 'POST', signal,
      body: { expectedRevision: revision, expectedProfileRevision: baseline?.profile.revision ?? 0, name, description, enabled, imageReference, initializationScript: initializationScript || null, cpu, memoryMiB, pids: pids ?? 64, maxCommandSeconds, idleTtlSeconds, maxLifetimeSeconds, storageBytes, registryCredentialId: registryCredentialId ?? null } });
    if (!saved?.profile?.id || !Number.isSafeInteger(saved.profile.revision) || !Number.isSafeInteger(saved.securityRevision)) throw new IdentityCompletionError();
    return saved;
  } catch (error) { if (error instanceof IdentityCompletionError) return null; throw error; }
}
export const blankTenantSandbox = (): SandboxProfile => ({ id: '', revision: 0, name: '', description: '', enabled: true, imageReference: '', initializationScript: null, cpu: 1, memoryMiB: 1024, pids: 64, maxCommandSeconds: 120, idleTtlSeconds: 1800, maxLifetimeSeconds: 7200, storageBytes: 250 * 1048576, lastVerifiedAt: null, verificationReason: null });
