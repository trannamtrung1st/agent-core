import { IdentityCompletionError, identityRequest } from './identity';
import { tenantAgentApi } from './tenantAgentConfiguration';

export type AgentLifecycle = { instanceId: string; agentName: string; organizationName: string; lifecycle: 'Active' | 'Disabled'; instanceRevision: number; securityRevision: number;
  decommissioned: boolean; canDecommission: boolean; canUpdate: boolean; blocker: 'system_managed_instance' | 'disabled_required' | 'agent_in_use' | 'browser_profile_busy' | 'agent_decommissioned' | null; operationId: string | null };
export type LifecycleAction = 'disable' | 'enable' | 'decommission';
export type LifecycleResult = { action: LifecycleAction; lifecycle: 'Active' | 'Disabled'; operationId?: string; cleanupStatus?: 'completed' | 'pending' };
export const agentLifecycleApi = (tenant: string, id: string) => tenantAgentApi(tenant, id, '/decommission');
export async function readAgentLifecycle(tenant: string, id: string, signal: AbortSignal): Promise<AgentLifecycle> {
  const value = await identityRequest<AgentLifecycle>(agentLifecycleApi(tenant, id), { signal });
  if (!value || value.instanceId !== id || typeof value.agentName !== 'string' || !value.agentName.trim() || typeof value.organizationName !== 'string' || !value.organizationName.trim() || !['Active','Disabled'].includes(value.lifecycle)
    || !Number.isSafeInteger(value.instanceRevision) || value.instanceRevision < 1 || !Number.isSafeInteger(value.securityRevision) || value.securityRevision < 1
    || typeof value.decommissioned !== 'boolean' || typeof value.canDecommission !== 'boolean' || typeof value.canUpdate !== 'boolean'
    || ![null,'system_managed_instance','disabled_required','agent_in_use','browser_profile_busy','agent_decommissioned'].includes(value.blocker)
    || value.operationId !== null && typeof value.operationId !== 'string'
    || value.canDecommission && (value.decommissioned || value.lifecycle !== 'Disabled' || value.blocker !== null)
    || value.decommissioned && (value.canUpdate || value.canDecommission)) throw new Error('Lifecycle facts could not be read safely.');
  return value;
}
export async function changeAgentLifecycle(tenant: string, id: string, observed: AgentLifecycle, action: LifecycleAction, operationId: string, signal: AbortSignal): Promise<LifecycleResult> {
  const expected = { expectedRevision: observed.securityRevision, expectedInstanceRevision: observed.instanceRevision };
  if (action === 'decommission') {
    const value = await identityRequest<{ operationId: string; status: string; cleanupStatus: string }>(tenantAgentApi(tenant,id), { method: 'DELETE', signal, body: { ...expected, operationId } });
    if (!value || value.operationId !== operationId || value.status !== 'decommissioned' || !['completed','pending'].includes(value.cleanupStatus)) throw new IdentityCompletionError();
    return { action, lifecycle: 'Disabled', operationId, cleanupStatus: value.cleanupStatus as 'completed' | 'pending' };
  }
  const lifecycle = action === 'disable' ? 'Disabled' : 'Active';
  const value = await identityRequest<{ instance: { instanceId: string; lifecycle: string; revision: number } }>(tenantAgentApi(tenant,id,'/lifecycle'), { method: 'PUT', signal, body: { ...expected, lifecycle: action === 'disable' ? 1 : 0 } });
  if (!value?.instance || value.instance.instanceId !== id || value.instance.lifecycle !== lifecycle || value.instance.revision !== observed.instanceRevision + 1) throw new IdentityCompletionError();
  return { action, lifecycle };
}
