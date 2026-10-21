import { tenantApi } from './identity';
import { tenantAgentApi } from './tenantAgentConfiguration';
export type TenantUserProfile = { profile: { tenantId: string; principalId: string; revision: number; updatedAt: string; preferences: Record<string, { value: string; source: 'UserSet'; updatedAt: string }> }; securityRevision: number };
export type MemoryScope = 'session' | 'identityUser' | 'user';
export type TenantMemoryItem = { memoryId: string; scope: MemoryScope; kind: string; status: 'Active' | 'Superseded' | 'Deleted' | 'Resolved'; subject: string; content: string; revision: number; createdAt: string; updatedAt: string; originAgentId: string; source: string; supersedesMemoryId?: string; sourceMemoryIds?: string[]; canForget: boolean };
export type TenantMemoryPage = { items: TenantMemoryItem[]; total: number; retainedCount: number; page: number; pageSize: number; securityRevision: number; canReset: boolean; agentName: string };
export const tenantProfileApi = (tenant: string) => tenantApi(tenant, '/me/profile');
export const tenantMemoryApi = (tenant: string, agent: string, scope: MemoryScope, session?: string, page = 1) => {
  const query = new URLSearchParams({ scope, page: String(page), pageSize: '25' }); if (session) query.set('sessionId', session);
  return tenantAgentApi(tenant, agent, '/memory') + '?' + query;
};
