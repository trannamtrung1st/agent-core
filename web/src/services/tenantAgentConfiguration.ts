import type { AdminAgentInstance, ExecutionBudgetPolicy, ExecutionBudgetLimits, SelfManagementView } from './adminApi';
import type { SettingsSection, SettingValue, VersionPreview } from './instanceConfiguration';
import { identityRequest, tenantApi } from './identity';
import type { InstanceSkill } from './instanceSkills';
import type { InstanceResource } from './instanceConfiguration';

export type TenantAgentEditor = { agent: { principalId: string; systemIdentityId: string; instance: AdminAgentInstance & { persona: { name: string; role: string; description: string; tone: string } } }; settings: SettingsSection[]; securityRevision: number; canUpdate: boolean; unavailableSettings: string[] };
export type TenantAgentControlsView = Pick<TenantAgentEditor, 'agent' | 'securityRevision' | 'canUpdate'> & {
  budgets: { instanceRevision: number; securityRevision: number; overrides: ExecutionBudgetPolicy | null; definitionDefaults: ExecutionBudgetPolicy | null; hostLimits: ExecutionBudgetLimits };
  management: { policy: SelfManagementView; securityRevision: number };
};
export const tenantAgentApi = (tenant: string, id: string, suffix = '') => tenantApi(tenant, `/agents/${encodeURIComponent(id)}${suffix}`);
export const readTenantAgentEditor = (tenant: string, id: string, signal?: AbortSignal) => identityRequest<TenantAgentEditor>(tenantAgentApi(tenant, id, '/editor'), { signal });
export async function patchTenantAgentSettings(tenant: string, id: string, section: SettingsSection, set: Record<string, SettingValue>, clear: string[], signal?: AbortSignal): Promise<SettingsSection> {
  if (section.securityRevision === undefined) throw new Error('Read current organization settings before saving.');
  const saved = await identityRequest<{ instance: AdminAgentInstance; securityRevision: number }>(tenantAgentApi(tenant, id, `/settings/${encodeURIComponent(section.section)}`), { method: 'PATCH', signal,
    body: { expectedRevision: section.securityRevision, expectedInstanceRevision: section.instanceRevision, set, clear } });
  return { ...section, instanceRevision: saved.instance.revision, securityRevision: saved.securityRevision };
}

export type TenantAgentVersion = { version: number; status: string; metadataRevision: number | null };
export type TenantAgentVersionCatalog = Pick<TenantAgentEditor, 'agent' | 'securityRevision'> & { items: TenantAgentVersion[]; total: number; page: number; pageSize: number; canAdopt: boolean };
export type TenantAgentVersionPreview = { preview: VersionPreview; securityRevision: number; targetPublicationRevision: number | null; canAdopt: boolean; childAccess: { skills: boolean; resources: boolean; allSkills: boolean; allResources: boolean } };
export type TenantSkillItem = { skill: InstanceSkill; canManage: boolean; canCustomize: boolean };
export type TenantSkillEditor = Pick<TenantAgentEditor, 'agent' | 'securityRevision'> & { items: TenantSkillItem[]; total: number; page: number; pageSize: number; canCreate: boolean; allReadable: boolean };
export type TenantSkillDetail = Pick<TenantAgentEditor, 'agent' | 'securityRevision'> & { item: TenantSkillItem };
export type TenantResourceItem = { resource: InstanceResource; canManage: boolean; canDelete: boolean; canCopy: boolean };
export type TenantResourceEditor = Pick<TenantAgentEditor, 'agent' | 'securityRevision'> & { items: TenantResourceItem[]; total: number; page: number; pageSize: number; canCreate: boolean; allReadable: boolean };
export type TenantResourceDetail = Pick<TenantAgentEditor, 'agent' | 'securityRevision'> & { item: TenantResourceItem };
