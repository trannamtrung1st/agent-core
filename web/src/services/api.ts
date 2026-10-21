import { createTenantChat, deleteTenantChat, tenantChatCandidates, tenantChatCatalog, tenantChatHistory, tenantChatModels, tenantChatMutation, tenantSessionView } from './tenantChat';

export type AutomationTarget = { kind: "backgroundSession" | "existingSession"; sessionId?: string | null };
export type AutomationDelivery = { kind: "none" | "toSession"; sessionId?: string | null };
export type AgentDescriptor = {
  id: string;
  version: number;
  name: string;
  role: string;
  description: string;
  voiceAvailable: boolean;
  language?: string;
};

export type HealthResponse = {
  status: string;
  profile: string;
  protocolVersion: number;
  scheduler?: { status: "starting" | "ready" | "degraded" | "stale"; lastPassAt: string | null; lastSuccessAt: string | null };
};

export type SessionModelChoice = {
  key?: string | null;
  reasoningEffort?: string | null;
};

export type SessionModelSelection = {
  catalogKey: string;
  displayName: string;
  selectionSource: string;
  reasoningEffort?: string | null;
  modelId?: string | null;
};

export type ModelDescriptor = {
  key: string;
  displayName: string;
  tools: boolean;
  vision: boolean;
  structuredOutput: boolean;
  reasoning: boolean;
  supportedReasoningEfforts: string[];
  defaultReasoningEffort?: string | null;
  contextCategory?: string | null;
  costCategory?: string | null;
};

export type ModelCatalog = {
  defaultKey: string;
  models: ModelDescriptor[];
};

export type SessionResponse = {
  sessionId: string;
  agentId: string;
  agentVersion: number;
  mode: string;
  pendingMode: string | null;
  status: string;
  lastEntrySequence?: number;
  pauseReason?: string | null;
  lifecycleStatus?: string | null;
  speechLocale?: { effective?: string; source?: string; override?: string | null } | null;
  model?: SessionModelSelection | null;
  agentName?: string | null;
  agentRole?: string | null;
  agentInstanceId?: string | null;
};

export type AgentRun = {
  taskIntent?: string | null;
  contextProvenanceAvailable?: boolean;
  contextManifest?: { contextFormatVersion: number; coreContractVersion: number; fingerprint: string; fieldSources?: Record<string, { kind: string; revision: string }> | null; sections: { id: string; layer: "Core" | "Runtime" | "Evidence"; source: string; semantics: string; sourceRevision: string | null; characters: number; utf8Bytes: number; estimatedTokens: number; omitted?: boolean; originalCharacters?: number | null }[] } | null;
  executionDiagnostic?: { remainingBudgetMs: number; lastTool: string | null; recordedToolResults: number; failedAttempts: { attempt: number; code: string; reason: string | null; channel: string | null; maxOutputTokens?: number | null; transport?: string | null }[];
    protocolRecovery?: { reason: string; channel: string | null; eligibility: string; strategy: string | null; outcome: string | null } | null } | null;
  composerInput?: {explicitSkillKeys:string[];provenance:string;admittedAt:string;error:string|null;references:{kind:string;locator:string;label:string;status:string;revision:number|null;sha256:string|null;truncated:boolean}[]} | null;
  configuration?: { definitionId: string; definitionVersion: number; instanceRevision: number; personaRevision: number; configurationHash: string; resources: { key: string; logicalPath: string; virtualPath: string; contentSha256: string; byteLength: number }[] } | null;
  triggerOrigin?: { triggerRevision?: number | null; triggerId: string | null; kind: string; source: { kind: string; key?: string | null; eventId?: string | null } | null; summary: string } | null;
  budget?: { class: string; source: string; maxSteps: number; durationSeconds: number; perToolSeconds: number; sandboxCommandSeconds?: number; stepsConsumed: number; activeExecutionMs: number; phase: string; terminationReason: string | null; cleanupStatus: string; closureConfirmed: boolean; closureRequested?: boolean | null; logoutRequested?: boolean | null; logoutVerified?: boolean; cleanupBlocked?: boolean } | null;
  agentRunId: string; sessionId: string; activationId: string; activationKind: string;
  status: string; revision: number; attemptCount: number; maxAttempts: number;
  cancellationRequested: boolean; cancellationAvailable: boolean; progress: string | null;
  wait?: { mode: string; until: string; backgroundSessionIds: string[]; deadline: string } | null;
  nextRetryAt: string | null; createdAt: string; updatedAt: string;
  approval: { approvalId: string; revision: number; actionHash: string; toolName: string; preview: string; expiresAt: string } | null;
  outcome: { kind: string; summary: string; outcomeEntryId: string | null; attentionRequired: boolean } | null;
  failureCode: string | null; failureSummary: string | null; diagnosticId: string | null;
  knownEffectSummary: string | null; modelCatalogKey: string; responseId: string | null;
  automationId: string | null; experienceId: string | null; sourceOccurrenceId: string | null; sourceBackgroundSessionId?: string | null;
};
export type CursorPage<T> = { items: T[]; nextCursor: string | null; hasMore: boolean };
export type BackgroundSession = {
  session: CatalogItem;
  origin: { kind: string; initialAgentRunId: string | null; parentSessionId: string | null; parentAgentRunId: string | null;
    automationId: string | null; occurrenceId: string | null; reportCompletion: boolean };
  completionDelivery?: { status: string; targetSessionId: string | null; parentAgentRunId: string | null; reason: string | null };
  originalTitle?: string | null; surfaces: string[]; initialRun: AgentRun | null; canContinueInChat: boolean; securityRevision?: number; artifactCount: number | null; artifactCountHasMore: boolean;
};

export type SessionAutomation = {
  automationId: string;
  instructions: string;
  status: string;
  triggerKind: string;
  timeZone: string | null;
  when: string;
  nextOccurrenceAt: string | null;
  revision: number;
  executionTarget?: AutomationTarget;
  completionDelivery?: AutomationDelivery;
  agentInstanceId?: string;
  suspensionReason?: string | null;
};

export type HistoryPage = {
  items: unknown[];
  nextAfter: number;
  hasMore: boolean;
  hasOlder?: boolean;
  nextBefore?: number | null;
};

export type SessionHistoryQuery = {
  after?: number;
  before?: number;
  limit?: number;
  signal?: AbortSignal;
};

export type CatalogItem = {
  canManage?: boolean;
  agentInstanceId?: string;
  sessionId: string;
  title: string;
  agentId: string;
  agentVersion: number;
  status: string;
  archived: boolean;
  ended: boolean;
  workspaceOwned: boolean;
  runtimeEpoch: number;
  revision: number;
  createdAt: string;
  updatedAt: string;
  pauseReason?: string | null;
  lifecycleStatus?: string | null;
  agentName?: string | null;
  agentRole?: string | null;
};

export type SessionPickerItem = Pick<CatalogItem, 'sessionId' | 'title' | 'updatedAt' | 'agentName' | 'agentId' | 'agentInstanceId' | 'archived' | 'ended' | 'status' | 'lifecycleStatus' | 'pauseReason'>;
export type SessionPickerSource = {
  list: (options: { limit: number; includeArchived: boolean; cursor: string | null }) => Promise<{ items: SessionPickerItem[]; hasMore: boolean; nextCursor: string | null }>;
  read: (id: string) => Promise<{ agentInstanceId?: string; status: string; lifecycleStatus?: string | null; pauseReason?: string | null }>;
};

export type CatalogPage = {
  items: CatalogItem[];
  nextCursor: string | null;
  hasMore: boolean;
};

export class CatalogRevisionConflictError extends Error {
  constructor(message = "Session changed. Review it and confirm delete again.") {
    super(message);
    this.name = "CatalogRevisionConflictError";
  }
}

export type ChatAgentInstance = {
  instanceId: string;
  definitionId: string;
  activeVersion: number;
  name: string;
  role: string;
  voiceAvailable: boolean;
  language: string;
};

export async function getHealth(): Promise<HealthResponse> {
  const response = await fetch("/health");
  if (!response.ok) {
    throw new Error("Unable to read health.");
  }

  return (await response.json()) as HealthResponse;
}

export type DrawerPageQuery = { limit?: number; before?: string; attentionOnly?: boolean };

export function drawerPageSearch(query?: DrawerPageQuery): string {
  const params = new URLSearchParams();
  if (query?.limit) params.set("limit", String(query.limit));
  if (query?.before) params.set("before", query.before);
  if (query?.attentionOnly) params.set("attentionOnly", "true");
  return params.size ? `?${params}` : "";
}
export type InstanceActivitySession = { session: CatalogItem; origin: string; surfaces: string[] };

export async function listChatAgentInstances(): Promise<ChatAgentInstance[]> { return (await tenantChatCandidates()).items; }
export const listModels = (): Promise<ModelCatalog> => tenantChatModels();
export const createSessionForInstance = (id: string, model?: SessionModelChoice | null): Promise<SessionResponse> => createTenantChat(id, model);
export async function endSession(id: string): Promise<void> { await tenantChatMutation(id, 'complete'); }
export const listCatalog = (options?: { cursor?: string | null; includeArchived?: boolean; limit?: number }): Promise<CatalogPage> => tenantChatCatalog(options);
export const renameSession = (id: string, title: string): Promise<CatalogItem> => tenantChatMutation(id, 'rename', title);
export const archiveSession = (id: string): Promise<CatalogItem> => tenantChatMutation(id, 'archive');
export const unarchiveSession = (id: string): Promise<CatalogItem> => tenantChatMutation(id, 'restore');
export const getSession = (id: string): Promise<SessionResponse> => tenantSessionView(id);
export const listSessionMessages = (id: string, query: SessionHistoryQuery = {}): Promise<HistoryPage> => tenantChatHistory(id, query);
export async function reopenSession(id: string): Promise<'reopened'> { await tenantChatMutation(id, 'resume'); return 'reopened'; }
export const durableDeleteSession = (id: string): Promise<void> => deleteTenantChat(id);
