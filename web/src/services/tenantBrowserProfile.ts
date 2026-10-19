import { IdentityCompletionError, identityRequest } from './identity';
import { tenantAgentApi } from './tenantAgentConfiguration';

export type TenantBrowserResetResult = { operationId: string; status: 'pending' | 'reset' | 'unknown'; reasonCode: string; requestedAt: string; recordedAt: string | null };
export type TenantBrowserProfile = { instanceId: string; instanceRevision: number; securityRevision: number; witness: string; profilePresent: boolean; liveViews: number;
  canReset: boolean; busy: boolean; lastReset: TenantBrowserResetResult | null; agentName: string; lifecycle: string };
export const tenantBrowserProfileApi = (tenant: string, id: string) => tenantAgentApi(tenant, id, '/browser/profile');
export async function resetTenantBrowserProfile(tenant: string, id: string, view: TenantBrowserProfile, operationId: string, signal: AbortSignal): Promise<TenantBrowserResetResult> {
  const value = await identityRequest<TenantBrowserResetResult>(tenantBrowserProfileApi(tenant, id) + '/reset', { method: 'POST', signal,
    body: { operationId, expectedRevision: view.securityRevision, expectedInstanceRevision: view.instanceRevision, witness: view.witness } });
  const reasons = { pending: 'browser_profile_reset_started', reset: 'browser_profile_reset_completed', unknown: 'browser_profile_reset_unknown' };
  if (!value || value.operationId !== operationId || !Object.hasOwn(reasons, value.status) || value.reasonCode !== reasons[value.status]
    || typeof value.requestedAt !== 'string' || !Number.isFinite(Date.parse(value.requestedAt))
    || (value.status === 'pending' ? value.recordedAt !== null : typeof value.recordedAt !== 'string' || !Number.isFinite(Date.parse(value.recordedAt)))) throw new IdentityCompletionError();
  return value;
}
