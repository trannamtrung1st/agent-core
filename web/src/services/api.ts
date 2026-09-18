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

export type SessionResponse = {
  sessionId: string;
  agentId: string;
  agentVersion: number;
  mode: string;
  pendingMode: string | null;
  status: string;
  lastEntrySequence?: number;
  pauseReason?: string | null;
};

export type HistoryPage = {
  items: unknown[];
  nextAfter: number;
  hasMore: boolean;
};

export type CatalogItem = {
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

export async function createSession(agentId: string, agentVersion?: number, mode = "text"): Promise<SessionResponse> {
  const response = await ownerFetch("/api/v2/sessions", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ agentId, agentVersion, mode })
  });
  if (!response.ok) {
    throw new Error("Unable to create a session.");
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
  after = 0,
  limit = 50
): Promise<HistoryPage> {
  const params = new URLSearchParams({ after: String(after), limit: String(limit) });
  const response = await ownerFetch(`/api/v1/sessions/${sessionId}/messages?${params}`);
  if (!response.ok) {
    throw new Error("Unable to load the conversation.");
  }

  return (await response.json()) as HistoryPage;
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
