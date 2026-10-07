import { ownerFetch } from './api';
import { adminProblemMessage } from './adminApi';
export type SkillInput = { name: string; description: string; procedure: string; projection: 'Always' | 'OnDemand'; enabled: boolean; requiredCapabilities: string[] };
export type InstanceSkill = SkillInput & { key: string; origin: 'Definition' | 'Instance'; revision: number; definitionVersion: number | null; sourceDefinitionId: string | null; sourceDefinitionVersion: number | null; sourceDefinitionSkillId: string | null; missingCapabilities: string[] };
const path = (id: string, key?: string) => `/api/v2/admin/agent-instances/${encodeURIComponent(id)}/skills${key ? '/' + encodeURIComponent(key) : ''}`;
async function request<T>(url: string, method = 'GET', body?: unknown): Promise<T> {
  const response = await ownerFetch(url, { method, ...(body !== undefined ? { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) } : {}) });
  if (!response.ok) throw await adminProblemMessage(response, 'The Skill could not be saved. Reload current revisions and try again.');
  return response.json() as Promise<T>;
}
export const listInstanceSkills = (id: string) => request<InstanceSkill[]>(path(id));
export const inspectInstanceSkill = (id: string, key: string) => request<InstanceSkill>(path(id, key));
export const createInstanceSkill = (id: string, input: SkillInput) => request<InstanceSkill>(path(id), 'POST', input);
export const updateInstanceSkill = (id: string, skill: InstanceSkill, input: SkillInput) => request<InstanceSkill>(path(id, skill.key), 'PATCH', { ...input, expectedRevision: skill.revision });
export const toggleInstanceSkill = (id: string, skill: InstanceSkill, enabled: boolean) => request<InstanceSkill>(path(id, skill.key) + '/enabled', 'PUT', { expectedRevision: skill.revision, enabled });
export const customizeInstanceSkill = (id: string, skill: InstanceSkill) => request<InstanceSkill>(path(id, skill.key) + '/customize', 'POST', { expectedRevision: skill.revision });
export const deleteInstanceSkill = (id: string, skill: InstanceSkill) => request<InstanceSkill>(path(id, skill.key) + `?expectedRevision=${skill.revision}`, 'DELETE');
