import { ownerFetch } from './api';
import { adminProblemMessage } from './adminApi';
export type SettingValue = string | number | boolean | string[] | null;
export type SettingsSection = { section: string; instanceRevision: number; definitionId: string; definitionVersion: number; overrides: Record<string, SettingValue>; effective: Record<string, SettingValue>; definitionDefaults: Record<string, SettingValue>; sources: Record<string, 'definition' | 'instance'>; configurationHash: string };
export type InstanceResource = { key: string; origin: 'Definition' | 'Instance'; logicalPath: string; kind: number; mediaType: string; contentSha256: string; byteLength: number; enabled: boolean; enabledOverride: boolean | null; revision: number; virtualPath: string; dependencies: string[]; sourceDefinitionId: string | null; sourceDefinitionVersion: number | null };
export type ResourceCatalog = { instanceRevision: number; definitionId: string; definitionVersion: number; resources: InstanceResource[] };
const root = (id: string) => `/api/v2/admin/agent-instances/${encodeURIComponent(id)}`;
async function request<T>(url: string, method = 'GET', body?: unknown): Promise<T> {
  const response = await ownerFetch(url, { method, ...(body instanceof FormData ? { body } : body !== undefined ? { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) } : {}) });
  if (!response.ok) throw await adminProblemMessage(response, 'The Instance changed or could not be saved. Reload while retaining your draft.');
  return response.json() as Promise<T>;
}
export const listInstanceSettings = (id: string) => request<SettingsSection[]>(`${root(id)}/settings`);
export const patchInstanceSettings = (id: string, section: SettingsSection, set: Record<string, SettingValue>, clear: string[]) => request<SettingsSection>(`${root(id)}/settings/${section.section}`, 'PATCH', { expectedInstanceRevision: section.instanceRevision, set, clear });
export const listInstanceResources = (id: string) => request<ResourceCatalog>(`${root(id)}/resources`);
export const uploadInstanceResource = (id: string, catalog: ResourceCatalog, form: FormData, resource?: InstanceResource) => {
  form.set('expectedInstanceRevision', String(catalog.instanceRevision));
  if (resource) form.set('expectedRevision', String(resource.revision));
  return request<ResourceCatalog>(`${root(id)}/resources${resource ? '/' + encodeURIComponent(resource.key) : ''}`, resource ? 'PATCH' : 'POST', form);
};
export const toggleInstanceResource = (id: string, catalog: ResourceCatalog, resource: InstanceResource, enabled: boolean | null) => enabled === null
  ? request<ResourceCatalog>(`${root(id)}/resources/${encodeURIComponent(resource.key)}/enabled-override?expectedInstanceRevision=${catalog.instanceRevision}&expectedRevision=${resource.revision}`, 'DELETE')
  : request<ResourceCatalog>(`${root(id)}/resources/${encodeURIComponent(resource.key)}/enabled`, 'PUT', { expectedInstanceRevision: catalog.instanceRevision, expectedRevision: resource.revision, enabled });
export const deleteInstanceResource = (id: string, catalog: ResourceCatalog, resource: InstanceResource) => request<ResourceCatalog>(`${root(id)}/resources/${encodeURIComponent(resource.key)}?expectedInstanceRevision=${catalog.instanceRevision}&expectedRevision=${resource.revision}`, 'DELETE');
export async function downloadInstanceResource(id: string, resource: InstanceResource): Promise<void> {
  const response = await ownerFetch(`${root(id)}/resources/${encodeURIComponent(resource.key)}/content`);
  if (!response.ok) throw await adminProblemMessage(response, 'Resource download failed. Retry.');
  const url = URL.createObjectURL(await response.blob());
  const link = document.createElement('a'); link.href = url; link.download = resource.logicalPath.split('/').at(-1) ?? 'resource'; link.click();
  URL.revokeObjectURL(url);
}

export async function readInstanceResourceFile(id: string, resource: InstanceResource): Promise<File> {
  const response = await ownerFetch(`${root(id)}/resources/${encodeURIComponent(resource.key)}/content`);
  if (!response.ok) throw await adminProblemMessage(response, 'Resource content could not be read. Reload before saving.');
  return new File([await response.blob()], resource.logicalPath.split('/').at(-1) ?? 'resource', { type: resource.mediaType });
}

export const copyInstanceResource = (id: string, catalog: ResourceCatalog, resource: InstanceResource) => request<ResourceCatalog>(`${root(id)}/resources/${encodeURIComponent(resource.key)}/copy`, 'POST', { expectedInstanceRevision: catalog.instanceRevision, logicalPath: resource.logicalPath });
export type VersionPreview = { instanceRevision: number; currentVersion: number; targetVersion: number; changedDefinitionFields: string[]; preservedInstanceOverrides: string[]; newInheritedItems: string[]; removedInheritedItems: string[]; activationNotice: string; changedInheritedItems?: string[] };
export const previewInstanceVersion = (id: string, version: number) => request<VersionPreview>(`${root(id)}/active-version/preview?version=${version}`);
