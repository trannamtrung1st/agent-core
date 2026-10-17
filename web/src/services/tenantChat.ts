import { identityRequest as identityCall, tenantApi, type TenantContext } from './identity';
import type { CatalogItem, CatalogPage, ChatAgentInstance, HistoryPage, ModelCatalog, SessionHistoryQuery, SessionModelChoice, SessionResponse } from './api';

const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
// Tenant Chat crosses document boundaries on entry/exit/switch. This context cannot change under a
// live connection, an admitted command or an outstanding request in this document.
export const chatTenantId = new RegExp(`^/t/(${uuid})(?:/?$|/c/${uuid}/?$)`, 'i').exec(window.location.pathname)?.[1].toLowerCase() ?? null;
type Metadata = { tenantId: string; sessionId: string; agentInstanceId: string; title: string; mode: string; status: string; lifecycleStatus: string;
  revision: number; createdAt: string; updatedAt: string; archivedAt: string | null; agentId: string; agentVersion: number; agentName: string; agentRole: string; canManage: boolean };
export type TenantChatCandidate = ChatAgentInstance & { revision: number; description: string };
export type TenantChatCandidates = { items: TenantChatCandidate[]; total: number; page: number; pageSize: number; securityRevision: number; canCreateSessions: boolean };
let requests = new AbortController();
async function request<T>(url: string, options: { method?: string; body?: unknown; signal?: AbortSignal } = {}): Promise<T> {
  const controller = requests;
  const value = await identityCall<T>(url, { ...options, signal: options.signal ? AbortSignal.any([options.signal, controller.signal]) : controller.signal });
  controller.signal.throwIfAborted(); return value;
}
export function retireTenantChatRequests() {
  requests.abort(); requests = new AbortController(); candidates.clear(); manageHints.clear(); createAllowed = false;
}
const candidates = new Map<string, TenantChatCandidate>();
let createAllowed = false;
const manageHints = new Map<string, boolean>();
function path(suffix: string) { if (!chatTenantId) throw new Error('Choose an organization before opening Chat.'); return tenantApi(chatTenantId, suffix); }
export const tenantChatAccess = () => request<TenantContext>(path('/access/me'));
export const tenantChatModels = () => request<ModelCatalog>(path('/chat/models'));
export async function tenantSessionMetadata(id: string) {
  const value = await request<Metadata>(path(`/sessions/${encodeURIComponent(id)}`)); manageHints.set(id, value.canManage); return value;
}
export const tenantMayManageSession = (id: string | null) => id != null && manageHints.get(id) === true;
export const tenantRunControl = (id: string) => request<{ canCancel: boolean }>(path(`/agent-runs/${encodeURIComponent(id)}`));
export async function tenantChatCandidates(page = 0, query = ''): Promise<TenantChatCandidates> {
  const result = await request<TenantChatCandidates>(path(`/chat/agents?page=${page}&pageSize=30&q=${encodeURIComponent(query)}`));
  for (const candidate of result.items) candidates.set(candidate.instanceId, candidate);
  while (candidates.size > 128) candidates.delete(candidates.keys().next().value!);
  createAllowed = result.canCreateSessions;
  return result;
}
export function tenantCanCreateChat() { return createAllowed; }
export async function tenantSessionView(id: string): Promise<SessionResponse> {
  const [view] = await Promise.all([request<SessionResponse>(path(`/sessions/${encodeURIComponent(id)}/conversation`)), tenantSessionMetadata(id)]);
  return view;
}
export async function createTenantChat(instanceId: string, model?: SessionModelChoice | null): Promise<SessionResponse> {
  const selected = candidates.get(instanceId);
  if (!selected || !createAllowed) throw new Error('Select an available agent before starting a conversation.');
  const access = await tenantChatAccess();
  const created = await request<Metadata>(path('/sessions'), { method: 'POST', body: { agentInstanceId: instanceId,
    expectedRevision: access.revision, expectedInstanceRevision: selected.revision, mode: 'text', modelKey: model?.key, reasoningEffort: model?.reasoningEffort } });
  return tenantSessionView(created.sessionId);
}
function catalogItem(row: Metadata): CatalogItem {
  manageHints.set(row.sessionId, row.canManage);
  while (manageHints.size > 128) manageHints.delete(manageHints.keys().next().value!);
  return { ...row, archived: row.archivedAt !== null, ended: row.status === 'ended', workspaceOwned: false, runtimeEpoch: 0 };
}
export async function tenantChatCatalog(options?: { cursor?: string | null; includeArchived?: boolean; limit?: number }): Promise<CatalogPage> {
  const page = options?.cursor ? Number(options.cursor) : 0;
  const size = options?.limit ?? 50;
  const response = await request<{ items: Metadata[]; total: number }>(path(`/sessions?page=${page}&pageSize=${size}&includeArchived=${Boolean(options?.includeArchived)}`));
  return { items: response.items.map(catalogItem), nextCursor: (page + 1) * size < response.total ? String(page + 1) : null, hasMore: (page + 1) * size < response.total };
}
export function tenantChatHistory(id: string, query: SessionHistoryQuery = {}): Promise<HistoryPage> {
  const params = new URLSearchParams({ limit: String(query.limit ?? 50) });
  if (query.after != null) params.set('after', String(query.after)); if (query.before != null) params.set('before', String(query.before));
  return request(path(`/sessions/${encodeURIComponent(id)}/messages?${params}`), { signal: query.signal });
}
export async function tenantChatMutation(id: string, action: string, title?: string): Promise<CatalogItem> {
  const [meta, access] = await Promise.all([tenantSessionMetadata(id), tenantChatAccess()]);
  const body = { expectedRevision: access.revision, expectedSessionRevision: meta.revision, ...(title !== undefined ? { title } : {}) };
  const row = await request<Metadata>(path(`/sessions/${encodeURIComponent(id)}${action === 'rename' ? '' : `/${action}`}`), { method: action === 'rename' ? 'PATCH' : 'POST', body });
  return catalogItem(row);
}
export async function deleteTenantChat(id: string): Promise<void> {
  const [meta, access] = await Promise.all([tenantSessionMetadata(id), tenantChatAccess()]);
  await request(path(`/sessions/${encodeURIComponent(id)}?expectedRevision=${access.revision}&expectedSessionRevision=${meta.revision}`), { method: 'DELETE' });
}
export async function tenantTextWitness(id: string) {
  const [view, access] = await Promise.all([tenantSessionMetadata(id), tenantChatAccess()]);
  return { expectedRevision: access.revision, expectedSessionRevision: view.revision };
}
export async function tenantCancelWitness(run: string) {
  const [saved, access] = await Promise.all([request<{ revision: number }>(path(`/agent-runs/${encodeURIComponent(run)}`)), tenantChatAccess()]);
  return { expectedRevision: access.revision, expectedRunRevision: saved.revision };
}
