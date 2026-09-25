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
  instanceRevision: number;
  personaRevision: number;
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

export async function listAdminToolNames(): Promise<string[]> {
  const response = await ownerFetch("/api/v2/admin/tools");
  if (!response.ok) {
    throw new Error(`Admin tool registry failed (${response.status})`);
  }
  const payload = (await response.json()) as { toolNames: string[] };
  return payload.toolNames;
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
    throw new Error(await adminProblemMessage(response, `Admin publish draft failed (${response.status})`));
  }
  return (await response.json()) as AdminDefinitionPublicationSummary;
}

export type AdminDefinitionValidationFinding = {
  field: string;
  code: string;
  message: string;
  severity: string;
};

export type AdminDefinitionDraftValidation = {
  draftId: string;
  draftRevision: number;
  configurationFingerprint: string;
  hasBlockingFindings: boolean;
  findings: AdminDefinitionValidationFinding[];
};

export async function validateAdminDefinitionDraft(
  draftId: string
): Promise<AdminDefinitionDraftValidation> {
  const response = await ownerFetch(`/api/v2/admin/definition-drafts/${draftId}/validate`, {
    method: "POST"
  });
  if (!response.ok) {
    throw new Error(await adminProblemMessage(response, `Admin validate draft failed (${response.status})`));
  }
  return (await response.json()) as AdminDefinitionDraftValidation;
}

export type AdminDefinitionDiffSection = {
  sectionId: string;
  label: string;
  changeKind: string;
  beforeSummary: string | null;
  afterSummary: string | null;
};

export type AdminDefinitionDraftDiff = {
  draftId: string;
  draftRevision: number;
  baselineKind: string;
  baselineVersion: number | null;
  sections: AdminDefinitionDiffSection[];
};

export async function getAdminDefinitionDraftDiff(draftId: string): Promise<AdminDefinitionDraftDiff> {
  const response = await ownerFetch(`/api/v2/admin/definition-drafts/${draftId}/diff`);
  if (!response.ok) {
    throw new Error(await adminProblemMessage(response, `Admin draft diff failed (${response.status})`));
  }
  return (await response.json()) as AdminDefinitionDraftDiff;
}

export type AdminDefinitionEvaluationScenario = {
  scenarioId: string;
  scenarioVersion: number;
  title: string;
  prompt: string;
  requirementLevel: "Required" | "Advisory";
  checkType: "ToolOffered" | "ToolNotOffered";
  toolName: string | null;
  updatedAt: string;
};

export type AdminDefinitionEvaluationResult = {
  draftId: string;
  draftRevision: number;
  configurationFingerprint: string;
  scenarioId: string;
  scenarioVersion: number;
  runtimeKind: string;
  passed: boolean;
  findings: string[];
  recordedAt: string;
};

export async function listAdminDefinitionEvaluationScenarios(
  draftId: string
): Promise<AdminDefinitionEvaluationScenario[]> {
  const response = await ownerFetch(`/api/v2/admin/definition-drafts/${draftId}/evaluation-scenarios`);
  if (!response.ok) {
    throw new Error(await adminProblemMessage(response, `Admin evaluation scenarios failed (${response.status})`));
  }
  return (await response.json()) as AdminDefinitionEvaluationScenario[];
}

export async function upsertAdminDefinitionEvaluationScenario(
  draftId: string,
  body: {
    expectedRevision: number;
    scenarioId: string;
    title: string;
    prompt: string;
    requirementLevel: string;
    checkType: string;
    toolName: string | null;
  }
): Promise<AdminDefinitionEvaluationScenario> {
  const response = await ownerFetch(`/api/v2/admin/definition-drafts/${draftId}/evaluation-scenarios`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body)
  });
  if (!response.ok) {
    throw new Error(await adminProblemMessage(response, `Admin upsert evaluation scenario failed (${response.status})`));
  }
  return (await response.json()) as AdminDefinitionEvaluationScenario;
}

export async function removeAdminDefinitionEvaluationScenario(
  draftId: string,
  scenarioId: string,
  expectedRevision: number
): Promise<void> {
  const response = await ownerFetch(
    `/api/v2/admin/definition-drafts/${draftId}/evaluation-scenarios/${encodeURIComponent(scenarioId)}?expectedRevision=${expectedRevision}`,
    { method: "DELETE" }
  );
  if (!response.ok) {
    throw new Error(await adminProblemMessage(response, `Admin remove evaluation scenario failed (${response.status})`));
  }
}

export async function runAdminDefinitionEvaluationScenario(
  draftId: string,
  scenarioId: string
): Promise<AdminDefinitionEvaluationResult> {
  const response = await ownerFetch(
    `/api/v2/admin/definition-drafts/${draftId}/evaluation-scenarios/${encodeURIComponent(scenarioId)}/run`,
    { method: "POST" }
  );
  if (!response.ok) {
    throw new Error(await adminProblemMessage(response, `Admin run evaluation scenario failed (${response.status})`));
  }
  return (await response.json()) as AdminDefinitionEvaluationResult;
}

export async function listAdminDefinitionEvaluationResults(
  draftId: string
): Promise<AdminDefinitionEvaluationResult[]> {
  const response = await ownerFetch(`/api/v2/admin/definition-drafts/${draftId}/evaluation-results`);
  if (!response.ok) {
    throw new Error(await adminProblemMessage(response, `Admin evaluation results failed (${response.status})`));
  }
  return (await response.json()) as AdminDefinitionEvaluationResult[];
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
  revision: number;
  personaRevision: number;
};

export type AdminPersonaUpdate = {
  expectedRevision: number;
  expectedPersonaRevision: number;
  name: string;
  role: string;
  description: string;
  tone: string;
};

function instanceMutationConflictMessage(status: number): string | null {
  return status === 409 ? "Instance revision conflict — reload and try again." : null;
}

async function adminProblemMessage(response: Response, fallback: string): Promise<string> {
  try {
    const problem = (await response.json()) as { title?: string; detail?: string };
    return problem.detail?.trim() || problem.title?.trim() || fallback;
  } catch {
    return fallback;
  }
}

export async function updateAdminAgentInstancePersona(
  instanceId: string,
  update: AdminPersonaUpdate
): Promise<AdminAgentInstance> {
  const response = await ownerFetch(`/api/v2/admin/agent-instances/${instanceId}/persona`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(update)
  });
  const conflict = instanceMutationConflictMessage(response.status);
  if (conflict) {
    throw new Error(conflict);
  }
  if (!response.ok) {
    throw new Error(`Admin persona update failed (${response.status})`);
  }
  return (await response.json()) as AdminAgentInstance;
}

export async function updateAdminAgentInstanceLifecycle(
  instanceId: string,
  expectedRevision: number,
  lifecycle: "Active" | "Archived"
): Promise<AdminAgentInstance> {
  const response = await ownerFetch(`/api/v2/admin/agent-instances/${instanceId}/lifecycle`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ expectedRevision, lifecycle })
  });
  const conflict = instanceMutationConflictMessage(response.status);
  if (conflict) {
    throw new Error(conflict);
  }
  if (!response.ok) {
    throw new Error(`Admin lifecycle update failed (${response.status})`);
  }
  return (await response.json()) as AdminAgentInstance;
}

export async function updateAdminAgentInstanceActiveVersion(
  instanceId: string,
  expectedRevision: number,
  version: number
): Promise<AdminAgentInstance> {
  const response = await ownerFetch(`/api/v2/admin/agent-instances/${instanceId}/active-version`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ expectedRevision, version })
  });
  const conflict = instanceMutationConflictMessage(response.status);
  if (conflict) {
    throw new Error(conflict);
  }
  if (!response.ok) {
    throw new Error(`Admin version update failed (${response.status})`);
  }
  return (await response.json()) as AdminAgentInstance;
}

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

export type AdminLearnedMemoryScope = "Session" | "IdentityUser" | "User";

export type AdminLearnedMemoryItem = {
  memoryId: string;
  kind: string;
  subject: string;
  content: string;
  provenance: {
    source: string;
    originSessionId: string | null;
    originMemoryId: string | null;
    recordedAt: string;
  };
  updatedAt: string;
};

export type AdminAutomationRegistration = {
  registrationId: string;
  intent: string;
  status: string;
  scheduleKind: string;
  timeZoneId: string;
  scheduleSummary: string;
  nextOccurrenceAtUtc: string | null;
  revision: number;
  suspensionReason: string | null;
  provenance: {
    authorizationOrigin: string;
    sourceSessionId: string | null;
    createdAt: string;
    updatedAt: string;
  };
};

export async function listAdminLearnedMemory(
  instanceId: string,
  scope: AdminLearnedMemoryScope,
  sessionId?: string
): Promise<AdminLearnedMemoryItem[]> {
  const params = new URLSearchParams({ scope });
  if (sessionId) {
    params.set("sessionId", sessionId);
  }
  const response = await ownerFetch(
    `/api/v2/admin/agent-instances/${instanceId}/learned-memory?${params.toString()}`
  );
  if (!response.ok) {
    throw new Error(
      await adminProblemMessage(response, `Admin learned memory list failed (${response.status})`)
    );
  }
  const payload = (await response.json()) as { items: AdminLearnedMemoryItem[] };
  return payload.items;
}

export async function deleteAdminLearnedMemory(
  instanceId: string,
  memoryId: string,
  scope: AdminLearnedMemoryScope,
  sessionId?: string
): Promise<void> {
  const params = new URLSearchParams({ scope, confirm: "true" });
  if (sessionId) {
    params.set("sessionId", sessionId);
  }
  const response = await ownerFetch(
    `/api/v2/admin/agent-instances/${instanceId}/learned-memory/${memoryId}?${params.toString()}`,
    { method: "DELETE" }
  );
  if (!response.ok) {
    throw new Error(
      await adminProblemMessage(response, `Admin learned memory delete failed (${response.status})`)
    );
  }
}

export async function resetAdminLearnedMemoryScope(
  instanceId: string,
  scope: AdminLearnedMemoryScope,
  sessionId?: string
): Promise<number> {
  const response = await ownerFetch(`/api/v2/admin/agent-instances/${instanceId}/learned-memory/reset`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ scope, sessionId: sessionId ?? null, confirm: true })
  });
  if (!response.ok) {
    throw new Error(
      await adminProblemMessage(response, `Admin learned memory reset failed (${response.status})`)
    );
  }
  const payload = (await response.json()) as { itemsRemoved: number };
  return payload.itemsRemoved;
}

export async function listAdminAutomationRegistrations(
  instanceId: string
): Promise<AdminAutomationRegistration[]> {
  const response = await ownerFetch(`/api/v2/admin/agent-instances/${instanceId}/automation/registrations`);
  if (!response.ok) {
    throw new Error(`Admin automation list failed (${response.status})`);
  }
  const payload = (await response.json()) as { items: AdminAutomationRegistration[] };
  return payload.items;
}

export async function cancelAdminAutomationRegistration(
  instanceId: string,
  registrationId: string,
  expectedRevision: number
): Promise<AdminAutomationRegistration> {
  const response = await ownerFetch(
    `/api/v2/admin/agent-instances/${instanceId}/automation/registrations/${registrationId}/cancel`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ expectedRevision, confirm: true })
    }
  );
  if (response.status === 409) {
    throw new Error(
      await adminProblemMessage(
        response,
        "Registration revision conflict — reload registrations and try again."
      )
    );
  }
  if (!response.ok) {
    throw new Error(`Admin automation cancel failed (${response.status})`);
  }
  return (await response.json()) as AdminAutomationRegistration;
}
