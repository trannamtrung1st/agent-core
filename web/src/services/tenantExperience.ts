import { tenantAgentApi } from './tenantAgentConfiguration';
export type ExperienceVisibility = 'Eligible' | 'Suppressed' | 'Superseded' | 'Deleted';
export type ExperienceContent = { goal: string; attempts: string[]; decisions: string[]; outcomes: string[]; corrections: string[]; unresolved: string[]; difficulties: string[]; lessons: string[] };
export type TenantExperienceItem = { experienceId: string; scope: 'AgentUser' | 'Instance'; visibility: ExperienceVisibility; content: ExperienceContent | null; revision: number; createdAt: string; updatedAt: string; sourceKind: 'Session' | 'AgentRun' | 'Consolidation'; sourceId: string; throughCursor: number; sourceExperienceIds: string[]; canManage: boolean };
export type TenantExperiencePage = { items: TenantExperienceItem[]; total: number; retainedCount: number; page: number; pageSize: number; securityRevision: number; agentName: string; canReset: boolean };
export const tenantExperienceApi = (tenant: string, agent: string, page = 1, historical = true) => tenantAgentApi(tenant, agent, '/experience') + '?' + new URLSearchParams({ page: String(page), pageSize: '25', includeHistorical: String(historical) });
