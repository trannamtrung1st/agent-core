import { preferenceFromFields, type ModelPreference } from './modelPreference';
import { identityRequest as identityCall, tenantApi, type TenantContext } from './identity';
import type { CatalogItem, CatalogPage, ChatAgentInstance, HistoryPage, ModelCatalog, SessionHistoryQuery, SessionModelChoice, SessionResponse } from './api';

const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
// Tenant Chat crosses document boundaries on entry/exit/switch. This context cannot change under a
// live connection, an admitted command or an outstanding request in this document.
export const chatTenantId = new RegExp(`^/t/(${uuid})(?:/?$|/c/${uuid}/?$)`, 'i').exec(window.location.pathname)?.[1].toLowerCase() ?? null;
type Metadata = { tenantId: string; sessionId: string; agentInstanceId: string; title: string; mode: string; status: string; lifecycleStatus: string;
  agentCompletion: string; revision: number; createdAt: string; updatedAt: string; archivedAt: string | null; agentId: string; agentVersion: number; agentName: string; agentRole: string; canManage: boolean };
export type TenantChatCandidate = ChatAgentInstance & { revision: number; description: string };
export type TenantChatCandidates = { items: TenantChatCandidate[]; total: number; page: number; pageSize: number; securityRevision: number; canCreateSessions: boolean; emptyReason?: string | null; canManageSetup?: boolean; assistantSetupPending?: boolean };
let requests = new AbortController();
async function request<T>(url: string, options: { method?: string; body?: unknown; signal?: AbortSignal } = {}): Promise<T> {
  const controller = requests;
  const value = await identityCall<T>(url, { ...options, signal: options.signal ? AbortSignal.any([options.signal, controller.signal]) : controller.signal });
  controller.signal.throwIfAborted(); return value;
}
export function retireTenantChatRequests() {
  requests.abort(); requests = new AbortController(); candidates.clear(); manageHints.clear(); createAllowed = false; emptyReason = null; canManageSetup = false; assistantSetupPending = false;
}
const candidates = new Map<string, TenantChatCandidate>();
let createAllowed = false;
const manageHints = new Map<string, boolean>();
function path(suffix: string) { if (!chatTenantId) throw new Error('Choose an organization before opening Chat.'); return tenantApi(chatTenantId, suffix); }
export const tenantChatAccess = () => request<TenantContext>(path('/access/me'));
export async function tenantComposerSpace(instance: string, session?: string | null): Promise<string> {
  if (session) {
    const view = await request<import('./tenantSessionContext').SessionContextView>(path(`/sessions/${encodeURIComponent(session)}/context`));
    if (!view.space || view.state === 'stale') throw new Error('Choose an available Session Space before selecting its files.');
    return view.selection.spaceId;
  }
  const space = await request<import('./tenantSpaces').TenantSpace>(path(`/agents/${encodeURIComponent(instance)}/private-space`));
  return space.spaceId;
}
export const tenantComposerChoices = (instance: string, params: URLSearchParams) => request<{ items: import('./messageParts').ComposerChoice[]; nextCursor: string | null }>(path(`/agents/${encodeURIComponent(instance)}/composer?${params}`));
export const tenantChatModels = (instanceId?: string) => request<ModelCatalog>(path('/chat/models') + (instanceId ? `?instanceId=${encodeURIComponent(instanceId)}` : ''));
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
  emptyReason = result.emptyReason ?? null; canManageSetup = result.canManageSetup === true;
  assistantSetupPending = result.assistantSetupPending === true;
  return result;
}
let emptyReason: string | null = null; let canManageSetup = false;
let assistantSetupPending = false;
export function tenantChatSetupPending() { return assistantSetupPending; }
export function tenantChatCanManageSetup() { return canManageSetup; }
export async function retryTenantGeneralAssistantSetup() {
  const result = await request<{ status: 'ready' | 'setup_pending' }>(path('/general-assistant/reconcile'), { method: 'POST' });
  if (!result || !['ready', 'setup_pending'].includes(result.status)) throw new Error('Assistant setup outcome needs confirmation.');
  return result;
}
export function tenantChatEmptyReason() { return emptyReason; }
export function tenantCanCreateChat() { return createAllowed; }
export async function tenantSessionView(id: string): Promise<SessionResponse> {
  const [view] = await Promise.all([request<SessionResponse>(path(`/sessions/${encodeURIComponent(id)}/conversation`)), tenantSessionMetadata(id)]);
  return view;
}
export async function createTenantChat(instanceId: string, model?: SessionModelChoice | null, retentionKind: 'saved' | 'temporary' = 'saved'): Promise<SessionResponse> {
  if (!candidates.has(instanceId) || !createAllowed) throw new Error('Select an available agent before starting a conversation.');
  const current = await request<TenantChatCandidates>(path(`/chat/agents?instanceId=${encodeURIComponent(instanceId)}&pageSize=1`));
  const selected = current.items.find(item => item.instanceId === instanceId);
  createAllowed = current.canCreateSessions;
  if (!selected || !createAllowed) throw new Error('Selected agent became unavailable. Reload agents and choose a visible agent before starting a conversation.');
  const access = await tenantChatAccess();
  const created = await request<Metadata>(path('/sessions'), { method: 'POST', body: { agentInstanceId: instanceId,
    expectedRevision: access.revision, expectedInstanceRevision: selected.revision, retentionKind, mode: 'text', modelPreference: preferenceFromFields(model?.offeringId, model?.reasoningEffort) } });
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
export async function tenantInterruptWitness(id: string, run: string) {
  const [view, saved, access] = await Promise.all([tenantSessionMetadata(id), request<{ revision: number; canCancel: boolean; context: { sessionId: string } }>(path(`/agent-runs/${encodeURIComponent(run)}`)), tenantChatAccess()]);
  if (!saved.canCancel || saved.context.sessionId !== id) throw new Error('This response can no longer be interrupted. Check the current conversation before trying again.');
  return { expectedRevision: access.revision, expectedSessionRevision: view.revision, interruptedRunId: run, expectedRunRevision: saved.revision };
}

export async function tenantSelectChatModel(id: string, offeringId: string | null, reasoningEffort: string | null) {
  const witness = await tenantTextWitness(id);
  return request<{ sessionId: string; revision: number; model: SessionResponse['model']; modelPreference?: ModelPreference }>(path(`/sessions/${encodeURIComponent(id)}/model`), {
    method: 'PUT', body: { ...witness, modelPreference: preferenceFromFields(offeringId, reasoningEffort) }
  });
}

export async function tenantSetAgentCompletion(id: string, allowed: boolean) {
  const witness = await tenantTextWitness(id);
  return request<Metadata>(path(`/sessions/${encodeURIComponent(id)}/agent-completion`), { method: 'PUT', body: { ...witness, allowed } });
}
