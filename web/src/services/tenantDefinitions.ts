import { identityRequest, tenantApi, type Page } from './identity';
import type { DefinitionCandidate } from '../features/admin/definitionCandidate';
import type { AdminAuthoringOptions, ExecutionBudgetLimits, AdminCapabilityDescriptor } from './adminApi';

export type TenantDefinitionSummary = { definition: { tenantId: string; definitionId: string; createdAt: string }; displayName: string; draftCount: number; latestVersion: number | null };
export type TenantDraft = { draftId: string; definitionId: string; revision: number; candidate: DefinitionCandidate; sourceKind: number; sourceVersion: number | null; sourceDefinitionId: string | null; updatedAt: string };
export type TenantPublication = { definitionId: string; version: number; status: number; metadataRevision: number; payload: DefinitionCandidate; publishedAt: string };
export type TenantPublicationSummary = Omit<TenantPublication, 'payload'>;
export type TenantResource = { resourceId: string; logicalPath: string; kind: number; mediaType: string; byteLength: number; contentSha256: string };
export type DefinitionResourceAccess = { allRead: boolean; canCreate: boolean; canEvaluate: boolean; canPublish: boolean; items: { resourceId: string; canManage: boolean; canDelete: boolean }[] };
export type TenantDraftState = { draft: TenantDraft; resources: TenantResource[]; suites: import("./tenantEvaluations").EvaluationSuite[]; attempts: import("./tenantEvaluations").EvaluationAttempt[]; resourceAccess: DefinitionResourceAccess };
export type DefinitionAccess = { permissions: string[]; revision: number; canFork: Record<string, boolean> };
export type DefinitionMetadata = { authoring: AdminAuthoringOptions; limits: ExecutionBudgetLimits; capabilities: AdminCapabilityDescriptor[] };
export type DefinitionTemplate = { id: string; version: number; identity: { name: string; description: string } };
export const definitionApi = (tenant: string, id: string, suffix = '') => tenantApi(tenant, `/agent-definitions/${encodeURIComponent(id)}${suffix}`);
export const draftApi = (tenant: string, id: string, suffix = '') => tenantApi(tenant, `/definition-drafts/${encodeURIComponent(id)}${suffix}`);
export const listTenantDefinitions = (tenant: string, page: number, signal?: AbortSignal) => identityRequest<Page<TenantDefinitionSummary>>(tenantApi(tenant, `/agent-definitions?page=${page}&pageSize=20`), { signal });
export const sourceLabel = (draft: TenantDraft) => draft.sourceKind === 1 ? `Shipped ${draft.sourceDefinitionId} · v${draft.sourceVersion}` : draft.sourceKind === 2 ? `Publication v${draft.sourceVersion}` : 'New definition';
export const resourceKinds = ['Knowledge', 'Reference', 'Template', 'Static asset', 'Evaluation fixture'];
