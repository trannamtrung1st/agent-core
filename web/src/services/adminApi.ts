import { ownerFetch } from "./api";

export type AdminDefinitionInventoryItem = {
  definitionId: string;
  version: number;
  source: string;
  status: string;
  displayName: string;
};

export type AdminInstanceInventoryItem = {
  instanceId: string;
  definitionId: string;
  activeVersion: number;
  lifecycle: string;
  compatibility: boolean;
  personaName: string;
  createdAt: string;
  updatedAt: string;
};

export type AdminEffectiveConfiguration = {
  definitionSource: string;
  definitionId: string;
  definitionVersion: number;
  definitionStatus: string;
  instanceId: string;
  instanceLifecycle: string;
  compatibility: boolean;
  persona: { name: string; role: string; description: string; tone: string };
  providerPreferences: {
    languageModel: string;
    speechRecognizer: string | null;
    speechSynthesizer: string | null;
    interruptionClassifier: string;
  };
  effectiveModel: {
    catalogKey: string;
    displayName: string;
    selectionSource: string;
    reasoningEffort: string | null;
    modelId: string | null;
  };
  effectiveToolAllowlist: string[];
  harnessReferences: string[];
  workspaceTemplateId: string | null;
  knowledgeSources: Array<{ identity: string; title: string; citation: string }>;
  memoryPolicy: {
    sessionMemory: boolean;
    identityUserPromotion: boolean;
    identityUserRetrieval: boolean;
    userPromotion: boolean;
    userRetrieval: boolean;
  };
  triggerPolicy: {
    enabled: boolean;
    allowUserScheduling: boolean;
    allowOneShot: boolean;
    allowDaily: boolean;
    allowWeekly: boolean;
    allowIndefiniteRecurrence: boolean;
    maxActiveRegistrations: number;
    oneShotHorizonDays: number;
    minRecurrenceDays: number;
    allowedSourceKinds: string[];
    allowFixedInterval: boolean;
    minFixedIntervalSeconds: number;
  } | null;
  durableExecutionEligibility: {
    instanceActive: boolean;
    definitionResolved: boolean;
    triggerPolicyEnabled: boolean;
    allowsScheduleSource: boolean;
    allowsApplicationEventSource: boolean;
    canAcceptNewTriggeredWork: boolean;
  };
};

export async function listAdminDefinitions(): Promise<AdminDefinitionInventoryItem[]> {
  const response = await ownerFetch("/api/v2/admin/definitions");
  if (!response.ok) {
    throw new Error(`Admin definitions failed (${response.status})`);
  }
  const payload = (await response.json()) as { items: AdminDefinitionInventoryItem[] };
  return payload.items;
}

export async function listAdminInstances(): Promise<AdminInstanceInventoryItem[]> {
  const response = await ownerFetch("/api/v2/admin/instances");
  if (!response.ok) {
    throw new Error(`Admin instances failed (${response.status})`);
  }
  const payload = (await response.json()) as { items: AdminInstanceInventoryItem[] };
  return payload.items;
}

export async function getAdminEffectiveConfig(instanceId: string): Promise<AdminEffectiveConfiguration> {
  const response = await ownerFetch(`/api/v2/admin/instances/${instanceId}/effective-config`);
  if (!response.ok) {
    throw new Error(`Admin effective config failed (${response.status})`);
  }
  return (await response.json()) as AdminEffectiveConfiguration;
}

export type AdminDefinitionDraftSummary = {
  draftId: string;
  definitionId: string;
  revision: number;
  sourceKind: string;
  sourceVersion: number | null;
  updatedAt: string;
};

export type AdminDefinitionDraft = AdminDefinitionDraftSummary & {
  createdAt: string;
  candidate: Record<string, unknown>;
};

export type AdminDefinitionPublicationSummary = {
  definitionId: string;
  version: number;
  status: string;
  metadataRevision: number;
  publishedAt: string;
};

export async function listAdminDefinitionDrafts(): Promise<AdminDefinitionDraftSummary[]> {
  const response = await ownerFetch("/api/v2/admin/definition-drafts");
  if (!response.ok) {
    throw new Error(`Admin definition drafts failed (${response.status})`);
  }
  const payload = (await response.json()) as { items: AdminDefinitionDraftSummary[] };
  return payload.items;
}

export async function getAdminDefinitionDraft(draftId: string): Promise<AdminDefinitionDraft> {
  const response = await ownerFetch(`/api/v2/admin/definition-drafts/${draftId}`);
  if (!response.ok) {
    throw new Error(`Admin definition draft failed (${response.status})`);
  }
  return (await response.json()) as AdminDefinitionDraft;
}

export async function forkAdminDefinitionDraft(
  definitionId: string,
  sourceVersion: number,
  sourceKind: "ForkBuiltIn" | "ForkDurable"
): Promise<AdminDefinitionDraft> {
  const response = await ownerFetch("/api/v2/admin/definition-drafts/fork", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ definitionId, sourceVersion, sourceKind })
  });
  if (!response.ok) {
    throw new Error(`Admin fork draft failed (${response.status})`);
  }
  return (await response.json()) as AdminDefinitionDraft;
}

export async function updateAdminDefinitionDraft(
  draftId: string,
  expectedRevision: number,
  candidate: Record<string, unknown>
): Promise<AdminDefinitionDraft> {
  const response = await ownerFetch(`/api/v2/admin/definition-drafts/${draftId}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ expectedRevision, candidate })
  });
  if (!response.ok) {
    const conflict = response.status === 409;
    throw new Error(conflict ? "Draft revision conflict — reload and try again." : `Admin update draft failed (${response.status})`);
  }
  return (await response.json()) as AdminDefinitionDraft;
}

export async function publishAdminDefinitionDraft(
  draftId: string,
  expectedRevision: number
): Promise<AdminDefinitionPublicationSummary> {
  const response = await ownerFetch(`/api/v2/admin/definition-drafts/${draftId}/publish`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ expectedRevision })
  });
  if (!response.ok) {
    throw new Error(`Admin publish draft failed (${response.status})`);
  }
  return (await response.json()) as AdminDefinitionPublicationSummary;
}

export async function listAdminDefinitionPublications(
  definitionId: string
): Promise<AdminDefinitionPublicationSummary[]> {
  const response = await ownerFetch(`/api/v2/admin/definitions/${encodeURIComponent(definitionId)}/publications`);
  if (!response.ok) {
    throw new Error(`Admin publications failed (${response.status})`);
  }
  const payload = (await response.json()) as { items: AdminDefinitionPublicationSummary[] };
  return payload.items;
}

export type AdminDefinitionDraftResource = {
  resourceId: string;
  logicalPath: string;
  kind: string;
  mediaType: string;
  contentSha256: string;
  byteLength: number;
  updatedAt: string;
};

export type AdminDefinitionPublicationResource = Omit<AdminDefinitionDraftResource, "updatedAt">;

export type AdminResourceContentStored = {
  contentSha256: string;
  byteLength: number;
  mediaType: string;
};

export type AdminAgentInstance = {
  instanceId: string;
  definitionId: string;
  activeVersion: number;
  compatibility: boolean;
  lifecycle: string;
};

export async function listAdminDraftResources(draftId: string): Promise<AdminDefinitionDraftResource[]> {
  const response = await ownerFetch(`/api/v2/admin/definition-drafts/${draftId}/resources`);
  if (!response.ok) {
    throw new Error(`Admin draft resources failed (${response.status})`);
  }
  const payload = (await response.json()) as { items: AdminDefinitionDraftResource[] };
  return payload.items;
}

export async function uploadAdminDraftResourceContent(
  draftId: string,
  bytes: Blob,
  mediaType: string
): Promise<AdminResourceContentStored> {
  const response = await ownerFetch(`/api/v2/admin/definition-drafts/${draftId}/resources/content`, {
    method: "POST",
    headers: { "Content-Type": mediaType },
    body: bytes
  });
  if (!response.ok) {
    throw new Error(`Admin resource upload failed (${response.status})`);
  }
  return (await response.json()) as AdminResourceContentStored;
}

export async function upsertAdminDraftResource(
  draftId: string,
  expectedRevision: number,
  logicalPath: string,
  kind: string,
  stored: AdminResourceContentStored,
  resourceId?: string
): Promise<AdminDefinitionDraftResource> {
  const response = await ownerFetch(`/api/v2/admin/definition-drafts/${draftId}/resources`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      expectedRevision,
      resourceId: resourceId ?? null,
      logicalPath,
      kind,
      mediaType: stored.mediaType,
      contentSha256: stored.contentSha256,
      byteLength: stored.byteLength
    })
  });
  if (!response.ok) {
    throw new Error(`Admin resource bind failed (${response.status})`);
  }
  return (await response.json()) as AdminDefinitionDraftResource;
}

export async function removeAdminDraftResource(
  draftId: string,
  resourceId: string,
  expectedRevision: number
): Promise<void> {
  const response = await ownerFetch(
    `/api/v2/admin/definition-drafts/${draftId}/resources/${resourceId}?expectedRevision=${expectedRevision}`,
    { method: "DELETE" }
  );
  if (!response.ok) {
    throw new Error(`Admin resource remove failed (${response.status})`);
  }
}

export async function listAdminPublicationResources(
  definitionId: string,
  version: number
): Promise<AdminDefinitionPublicationResource[]> {
  const response = await ownerFetch(
    `/api/v2/admin/definitions/${encodeURIComponent(definitionId)}/publications/${version}/resources`
  );
  if (!response.ok) {
    throw new Error(`Admin publication resources failed (${response.status})`);
  }
  const payload = (await response.json()) as { items: AdminDefinitionPublicationResource[] };
  return payload.items;
}

export async function createAdminAgentInstance(
  definitionId: string,
  version: number
): Promise<AdminAgentInstance> {
  const response = await ownerFetch("/api/v2/admin/agent-instances", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ definitionId, version })
  });
  if (!response.ok) {
    throw new Error(`Admin create instance failed (${response.status})`);
  }
  return (await response.json()) as AdminAgentInstance;
}
