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
  budget?: { class: string; source: string; maxSteps: number; durationSeconds: number; perToolSeconds: number; stepsConsumed: number; activeExecutionMs: number; phase: string; terminationReason: string | null; cleanupStatus: string; closureConfirmed: boolean; closureRequested?: boolean | null; logoutRequested?: boolean | null; logoutVerified?: boolean; cleanupBlocked?: boolean } | null;
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
  origin: { kind: string; initialAgentRunId: string; parentSessionId: string | null; parentAgentRunId: string | null;
    automationId: string | null; occurrenceId: string | null; reportCompletion: boolean };
  completionDelivery?: { status: string; targetSessionId: string | null; parentAgentRunId: string | null; reason: string | null };
  originalTitle?: string | null; surfaces: string[]; initialRun: AgentRun | null; canContinueInChat: boolean; artifactCount: number; artifactCountHasMore: boolean;
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

export type CatalogPage = {
  items: CatalogItem[];
  nextCursor: string | null;
  hasMore: boolean;
};

export const OWNER_STORAGE_KEY = "agent-core.owner-capability";

export class OwnerCapabilityError extends Error {
  constructor(message = "Local owner access is unavailable.") {
    super(message);
    this.name = "OwnerCapabilityError";
  }
}

export class CatalogRevisionConflictError extends Error {
  constructor(message = "Session changed. Review it and confirm delete again.") {
    super(message);
    this.name = "CatalogRevisionConflictError";
  }
}

export function clearOwnerCapability(): void {
  window.localStorage.removeItem(OWNER_STORAGE_KEY);
}

let capabilityRefresh: Promise<string> | null = null;

async function refreshOwnerCapability(): Promise<string> {
  if (!capabilityRefresh) {
    capabilityRefresh = issueOwnerCapability().finally(() => {
      capabilityRefresh = null;
    });
  }

  return capabilityRefresh;
}

export async function ensureOwnerCapability(): Promise<string> {
  const stored = window.localStorage.getItem(OWNER_STORAGE_KEY);
  if (stored) {
    return stored;
  }

  return refreshOwnerCapability();
}

async function issueOwnerCapability(): Promise<string> {
  const response = await fetch("/api/v1/local/owner-capability", { method: "POST" });
  if (!response.ok) {
    throw new OwnerCapabilityError();
  }

  const body = (await response.json()) as { token: string };
  window.localStorage.setItem(OWNER_STORAGE_KEY, body.token);
  return body.token;
}

export async function ownerFetch(input: string, init: RequestInit = {}, retried = false): Promise<Response> {
  const token = await ensureOwnerCapability();
  const headers = new Headers(init.headers);
  headers.set("X-AgentCore-Owner-Capability", token);
  const response = await fetch(input, { ...init, headers });
  if (response.status === 401 && !retried) {
    clearOwnerCapability();
    await refreshOwnerCapability();
    return ownerFetch(input, init, true);
  }

  if (response.status === 401 || response.status === 403) {
    clearOwnerCapability();
    throw new OwnerCapabilityError();
  }

  return response;
}

export function ownerHeaders(token: string): HeadersInit {
  return { "X-AgentCore-Owner-Capability": token };
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

export async function listChatAgentInstances(): Promise<ChatAgentInstance[]> {
  const response = await ownerFetch("/api/v2/agent-instances");
  if (!response.ok) {
    throw new Error(`Unable to list managed chat instances (${response.status}).`);
  }
  const payload = (await response.json()) as { items: ChatAgentInstance[] };
  return payload.items;
}

export async function listAgents(): Promise<AgentDescriptor[]> {
  const response = await fetch("/api/v1/agents");
  if (!response.ok) {
    throw new Error("Unable to list agents.");
  }

  const body = (await response.json()) as { agents: AgentDescriptor[] };
  return body.agents;
}

export async function getHealth(): Promise<HealthResponse> {
  const response = await fetch("/health");
  if (!response.ok) {
    throw new Error("Unable to read health.");
  }

  return (await response.json()) as HealthResponse;
}

export async function listModels(): Promise<ModelCatalog> {
  const response = await ownerFetch("/api/v2/models");
  if (!response.ok) {
    throw new Error("Unable to load models.");
  }

  return (await response.json()) as ModelCatalog;
}

export async function createSessionForInstance(
  agentInstanceId: string,
  mode = "text",
  speechLocale?: string | null,
  model?: SessionModelChoice | null
): Promise<SessionResponse> {
  const body: Record<string, unknown> = { agentInstanceId, mode };
  if (speechLocale) {
    body.speechLocale = speechLocale;
  }
  if (model) {
    body.model = model;
  }

  const response = await ownerFetch("/api/v2/sessions", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body)
  });
  if (!response.ok) {
    throw new Error("Unable to create a session.");
  }

  return (await response.json()) as SessionResponse;
}

export async function setSessionModel(
  sessionId: string,
  model: SessionModelChoice
): Promise<SessionResponse> {
  const response = await ownerFetch(`/api/v2/sessions/${sessionId}/model`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(model)
  });
  if (!response.ok) {
    let message = "Unable to update the model.";
    try {
      const problem = (await response.json()) as { title?: string; detail?: string };
      message = problem.detail?.trim() || problem.title?.trim() || message;
    } catch {
      // keep fallback
    }

    throw new Error(message);
  }

  return (await response.json()) as SessionResponse;
}

export async function setSpeechLocale(sessionId: string, locale: string | null): Promise<SessionResponse> {
  const response = await ownerFetch(`/api/v2/sessions/${sessionId}/speech-locale`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ locale })
  });
  if (!response.ok) {
    let message = "Unable to update the speech locale.";
    try {
      const problem = (await response.json()) as { title?: string; detail?: string };
      message = problem.detail?.trim() || problem.title?.trim() || message;
    } catch {
      // keep fallback
    }

    throw new Error(message);
  }

  return (await response.json()) as SessionResponse;
}

export async function endSession(sessionId: string): Promise<void> {
  const response = await ownerFetch(`/api/v1/sessions/${sessionId}`, { method: "DELETE" });
  if (!response.ok) {
    throw new Error("Unable to end the session.");
  }
}

export async function listCatalog(options?: {
  cursor?: string | null;
  includeArchived?: boolean;
  limit?: number;
}): Promise<CatalogPage> {
  const params = new URLSearchParams();
  if (options?.cursor) {
    params.set("cursor", options.cursor);
  }
  if (options?.includeArchived) {
    params.set("includeArchived", "true");
  }
  if (options?.limit) {
    params.set("limit", String(options.limit));
  }

  const query = params.toString();
  const response = await ownerFetch(`/api/v2/sessions${query ? `?${query}` : ""}`);
  if (!response.ok) {
    throw new Error("Unable to load sessions.");
  }

  return (await response.json()) as CatalogPage;
}

export async function renameSession(sessionId: string, title: string): Promise<CatalogItem> {
  const response = await ownerFetch(`/api/v2/sessions/${sessionId}/rename`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ title })
  });
  if (!response.ok) {
    throw new Error("Unable to rename the session.");
  }

  return (await response.json()) as CatalogItem;
}

export async function archiveSession(sessionId: string): Promise<CatalogItem> {
  const response = await ownerFetch(`/api/v2/sessions/${sessionId}/archive`, { method: "POST" });
  if (!response.ok) {
    throw new Error("Unable to archive the session.");
  }

  return (await response.json()) as CatalogItem;
}

export async function unarchiveSession(sessionId: string): Promise<CatalogItem> {
  const response = await ownerFetch(`/api/v2/sessions/${sessionId}/unarchive`, { method: "POST" });
  if (!response.ok) {
    throw new Error("Unable to unarchive the session.");
  }

  return (await response.json()) as CatalogItem;
}

export async function getSession(sessionId: string): Promise<SessionResponse> {
  const response = await ownerFetch(`/api/v1/sessions/${sessionId}`);
  if (!response.ok) {
    throw new Error("Unable to load the session.");
  }

  return (await response.json()) as SessionResponse;
}

export async function listSessionMessages(
  sessionId: string,
  query: SessionHistoryQuery = {}
): Promise<HistoryPage> {
  const params = new URLSearchParams();
  if (query.after != null) {
    params.set("after", String(query.after));
  }
  if (query.before != null) {
    params.set("before", String(query.before));
  }
  params.set("limit", String(query.limit ?? 50));
  const response = await ownerFetch(`/api/v1/sessions/${sessionId}/messages?${params}`, {
    signal: query.signal
  });
  if (!response.ok) {
    throw new Error("Unable to load the conversation.");
  }

  return (await response.json()) as HistoryPage;
}

export async function transitionLifecycle(
  sessionId: string,
  target: string,
  reason?: string
): Promise<CatalogItem> {
  const response = await ownerFetch(`/api/v2/sessions/${sessionId}/lifecycle`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ target, reason })
  });
  if (!response.ok) {
    throw new Error("Unable to update the session lifecycle.");
  }

  return (await response.json()) as CatalogItem;
}

export async function reopenSession(sessionId: string): Promise<"reopened" | "in-use"> {
  const response = await ownerFetch(`/api/v2/sessions/${sessionId}/reopen`, { method: "POST" });
  if (response.status === 409) {
    return "in-use";
  }
  if (!response.ok) {
    throw new Error("Unable to reopen the session.");
  }

  return "reopened";
}

export async function durableDeleteSession(sessionId: string): Promise<void> {
  const response = await ownerFetch(`/api/v2/sessions/${sessionId}`, { method: "DELETE" });
  if (!response.ok) {
    throw new Error("Unable to delete the session.");
  }
}

export async function deleteAllSessions(options?: { includeArchived?: boolean }): Promise<number> {
  const params = new URLSearchParams();
  if (options?.includeArchived) {
    params.set("includeArchived", "true");
  }

  const query = params.toString();
  const response = await ownerFetch(`/api/v2/sessions${query ? `?${query}` : ""}`, { method: "DELETE" });
  if (!response.ok) {
    throw new Error("Unable to delete all sessions.");
  }

  const body = (await response.json()) as { deletedCount: number };
  return body.deletedCount;
}

async function problemMessage(response: Response, fallback: string): Promise<string> {
  try {
    const problem = (await response.json()) as { title?: string; detail?: string };
    return problem.detail?.trim() || problem.title?.trim() || fallback;
  } catch {
    return fallback;
  }
}

export type DrawerPageQuery = { limit?: number; before?: string; attentionOnly?: boolean };

export function drawerPageSearch(query?: DrawerPageQuery): string {
  const params = new URLSearchParams();
  if (query?.limit) params.set("limit", String(query.limit));
  if (query?.before) params.set("before", query.before);
  if (query?.attentionOnly) params.set("attentionOnly", "true");
  return params.size ? `?${params}` : "";
}

export async function listSessionAutomations(sessionId: string, query?: DrawerPageQuery): Promise<SessionAutomation[]> {
  const response = await ownerFetch(`/api/v2/sessions/${sessionId}/automations${drawerPageSearch(query)}`);
  if (!response.ok) {
    throw new Error(await problemMessage(response, "Unable to load schedules."));
  }

  const body = (await response.json()) as { items: SessionAutomation[] };
  return body.items;
}

export async function cancelSessionAutomation(
  sessionId: string,
  automationId: string,
  expectedRevision: number
): Promise<SessionAutomation> {
  const response = await ownerFetch(`/api/v2/sessions/${sessionId}/automations/${automationId}/cancel`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ expectedRevision })
  });
  if (!response.ok) {
    throw new Error(await problemMessage(response, "Unable to cancel the schedule."));
  }

  return (await response.json()) as SessionAutomation;
}

async function runRequest<T>(path: string, body?: unknown): Promise<T> {
  const response = await ownerFetch(path, body === undefined ? undefined : {
    method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body)
  });
  if (!response.ok) throw new Error(await problemMessage(response, "Unable to update background work. Refresh and try again."));
  return await response.json() as T;
}
export async function listBackgroundSessions(instanceId: string, cursor?: string, limit = 20): Promise<CursorPage<BackgroundSession>> {
  const query = new URLSearchParams({ limit: String(limit) });
  if (cursor) query.set("cursor", cursor);
  return runRequest(`/api/v2/agent-instances/${instanceId}/background-sessions?${query}`);
}
export async function getBackgroundSession(sessionId: string): Promise<BackgroundSession> {
  return runRequest(`/api/v2/sessions/${sessionId}/background`);
}
export async function continueInChat(sessionId: string): Promise<{ sessionId: string }> {
  return runRequest(`/api/v2/sessions/${sessionId}/continue-in-chat`, {});
}
export async function listAgentRuns(sessionId: string, cursor?: string, limit = 20): Promise<CursorPage<AgentRun>> {
  return runRequest(`/api/v2/sessions/${sessionId}/agent-runs?${new URLSearchParams({ limit: String(limit), ...(cursor ? { before: cursor } : {}) })}`);
}
export type InstanceActivitySession = { session: CatalogItem; origin: string; surfaces: string[] };
export async function getInstanceSession(instanceId: string, sessionId: string): Promise<InstanceActivitySession> {
  return runRequest(`/api/v2/agent-instances/${instanceId}/sessions/${sessionId}`);
}
export async function listInstanceSessions(instanceId: string, cursor?: string): Promise<CursorPage<InstanceActivitySession>> {
  return runRequest(`/api/v2/agent-instances/${instanceId}/sessions?${new URLSearchParams({ limit: "20", ...(cursor ? { cursor } : {}) })}`);
}
export async function listInstanceAgentRuns(instanceId: string, cursor?: string, limit = 20): Promise<CursorPage<AgentRun>> {
  return runRequest(`/api/v2/agent-instances/${instanceId}/agent-runs?${new URLSearchParams({ limit: String(limit), ...(cursor ? { before: cursor } : {}) })}`);
}
export async function getInstanceAgentRun(instanceId: string, runId: string): Promise<AgentRun> {
  return runRequest(`/api/v2/agent-instances/${instanceId}/agent-runs/${runId}`);
}
export async function cancelAgentRun(item: AgentRun): Promise<AgentRun> {
  return runRequest(`/api/v2/sessions/${item.sessionId}/agent-runs/${item.agentRunId}/cancel`, { expectedRevision: item.revision });
}
export async function decideAgentRunApproval(item: AgentRun, decision: "approve" | "reject"): Promise<AgentRun> {
  if (!item.approval) throw new Error("This run no longer needs approval. Refresh to see its current state.");
  return runRequest(`/api/v2/sessions/${item.sessionId}/agent-runs/${item.agentRunId}/approvals/${item.approval.approvalId}/${decision}`,
    { expectedRevision: item.revision, expectedApprovalRevision: item.approval.revision, actionHash: item.approval.actionHash });
}
