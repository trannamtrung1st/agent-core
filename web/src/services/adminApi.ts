import type { AutomationTarget, AutomationDelivery } from "./api";
import { ownerFetch } from "./api";

export type AdminDefinitionInventoryItem = {
  definitionId: string;
  version: number;
  source: string;
  status: string;
  displayName: string;
  draftCount?: number;
};

export type AdminAuthoringModelOption = {
  key: string;
  displayName: string;
  supportedReasoningEfforts: string[];
  defaultReasoningEffort: string | null;
};

export type AdminAuthoringOptions = {
  languageModelAliases: string[];
  speechRecognizerAliases: string[];
  speechSynthesizerAliases: string[];
  defaultLanguageModelAlias: string | null;
  defaultSpeechRecognizerAlias: string | null;
  defaultSpeechSynthesizerAlias: string | null;
  defaultModelKey: string;
  models: AdminAuthoringModelOption[];
  interruptionClassifiers: string[];
};

export type AdminInstanceInventoryItem = {
  instanceId: string;
  definitionId: string;
  activeVersion: number;
  lifecycle: string;
  personaName: string;
  createdAt: string;
  updatedAt: string;
};

export type AdminEffectiveConfiguration = {
  browser?: {
    providerId: string;
    displayName: string;
    enabled: boolean;
    ready: boolean;
    profileMode: string;
    policyMode: string;
    supportedFeatures: string[];
    engine?: string;
    maxSnapshotBytes: number;
    maxCaptureBytes: number;
    maxDownloadBytes: number;
    limits?: { operationTimeoutMs: number; capturesPerScope: number; downloadsPerScope: number;
      findMatches: number; waitTimeoutMs: number; textInputLength: number; automaticSettleMs: number } | null;
  } | null;
  definitionSource: string;
  definitionId: string;
  definitionVersion: number;
  definitionStatus: string;
  instanceId: string;
  instanceLifecycle: string;
  instanceRevision: number;
  personaRevision: number;
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
  knowledgeSources: Array<{
    identity: string;
    title: string;
    citation: string;
    resolvedResourcePath?: string;
  }>;
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
    maxActiveRegistrations: number; allowEvents?: boolean;
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
  unattendedModelCatalogKey?: string | null;
  unattendedReasoningEffort?: string | null;
};

export async function listAdminDefinitions(): Promise<AdminDefinitionInventoryItem[]> {
  const response = await ownerFetch("/api/v2/admin/definitions");
  if (!response.ok) {
    throw await adminProblemMessage(response, `Admin definitions failed (${response.status})`);
  }
  const payload = (await response.json()) as { items: AdminDefinitionInventoryItem[] };
  return payload.items;
}

export async function getAdminDefinitionVersion(
  definitionId: string, version: number, source: string
): Promise<Record<string, unknown>> {
  const sourceKind = source === "builtIn" ? "ForkBuiltIn" : "ForkDurable";
  const response = await ownerFetch(
    `/api/v2/admin/definitions/${encodeURIComponent(definitionId)}/versions/${version}?sourceKind=${sourceKind}`
  );
  if (!response.ok) {
    throw await adminProblemMessage(response, `Definition version unavailable (${response.status})`);
  }
  return await response.json() as Record<string, unknown>;
}

export async function listAdminInstances(): Promise<AdminInstanceInventoryItem[]> {
  const response = await ownerFetch("/api/v2/admin/instances");
  if (!response.ok) {
    throw await adminProblemMessage(response, `Admin instances failed (${response.status})`);
  }
  const payload = (await response.json()) as { items: AdminInstanceInventoryItem[] };
  return payload.items;
}

export async function listAdminAuthoringOptions(): Promise<AdminAuthoringOptions> {
  const response = await ownerFetch("/api/v2/admin/authoring-options");
  if (!response.ok) {
    throw await adminProblemMessage(response, `Admin authoring options failed (${response.status})`);
  }
  return (await response.json()) as AdminAuthoringOptions;
}

export async function deleteAdminAgentInstance(instanceId: string, expectedRevision: number): Promise<void> {
  const response = await ownerFetch(`/api/v2/admin/agent-instances/${instanceId}`, {
    method: "DELETE",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ expectedRevision })
  });
  if (!response.ok) {
    throw await adminProblemMessage(
      response,
      "This instance could not be deleted. Archive it first, or remove the references listed by the server."
    );
  }
}

export async function deleteAdminDefinition(
  definitionId: string,
  witness: {
    drafts: Array<{ draftId: string; revision: number }>;
    publications: Array<{ version: number; metadataRevision: number }>;
  }
): Promise<void> {
  const response = await ownerFetch(`/api/v2/admin/definitions/${encodeURIComponent(definitionId)}`, {
    method: "DELETE",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(witness)
  });
  if (!response.ok) {
    throw await adminProblemMessage(
      response,
      "This definition could not be deleted. It may still be referenced, or it changed since this page loaded."
    );
  }
}

export type AdminCapabilityDescriptor = { name: string; category: string; summary: string; tags: string[]; discoverable: boolean; defaultProjectionClass: string; configured: boolean };
export type AdminToolRegistry = { toolNames: string[]; maxToolAllowlistEntries: number | null; capabilities?: AdminCapabilityDescriptor[] };

export async function getAdminToolRegistry(): Promise<AdminToolRegistry> {
  const response = await ownerFetch("/api/v2/admin/tools");
  if (!response.ok) {
    throw await adminProblemMessage(response, `Admin tool registry failed (${response.status})`);
  }
  const payload = (await response.json()) as AdminToolRegistry;
  if (!Array.isArray(payload.toolNames)) throw new Error("Admin capability catalog is unavailable. Retry loading it.");
  return payload;
}

export async function listAdminToolNames(): Promise<string[]> {
  return (await getAdminToolRegistry()).toolNames;
}

export async function getAdminEffectiveConfig(instanceId: string): Promise<AdminEffectiveConfiguration> {
  const response = await ownerFetch(`/api/v2/admin/instances/${instanceId}/effective-config`);
  if (!response.ok) {
    throw await adminProblemMessage(response, `Admin effective config failed (${response.status})`);
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
  skills?: Array<{
    id: string;
    name: string;
    requiredCapabilities: string[];
  }>;
};

export async function listAdminDefinitionDrafts(): Promise<AdminDefinitionDraftSummary[]> {
  const response = await ownerFetch("/api/v2/admin/definition-drafts");
  if (!response.ok) {
    throw await adminProblemMessage(response, `Admin definition drafts failed (${response.status})`);
  }
  const payload = (await response.json()) as { items: AdminDefinitionDraftSummary[] };
  return payload.items;
}

export async function getAdminDefinitionDraft(draftId: string): Promise<AdminDefinitionDraft> {
  const response = await ownerFetch(`/api/v2/admin/definition-drafts/${draftId}`);
  if (!response.ok) {
    throw await adminProblemMessage(response, `Admin definition draft failed (${response.status})`);
  }
  return (await response.json()) as AdminDefinitionDraft;
}

export async function createNewAdminDefinitionDraft(definitionId: string): Promise<AdminDefinitionDraft> {
  const response = await ownerFetch("/api/v2/admin/definition-drafts/new", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ definitionId })
  });
  if (!response.ok) {
    throw await adminProblemMessage(response, "The definition could not be created. Check the ID and try again.");
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
    throw await adminProblemMessage(response, "The draft could not be created from that version. Try again.");
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
    throw await adminProblemMessage(
      response,
      response.status === 409
        ? "This draft changed somewhere else. Reload it, then try again."
        : "The draft could not be saved. Check the form and try again."
    );
  }
  return (await response.json()) as AdminDefinitionDraft;
}

export async function deleteAdminDefinitionDraft(
  draftId: string,
  expectedRevision: number
): Promise<void> {
  const response = await ownerFetch(
    `/api/v2/admin/definition-drafts/${draftId}?expectedRevision=${expectedRevision}`,
    { method: "DELETE" }
  );
  if (!response.ok) {
    throw await adminProblemMessage(response, `Admin delete draft failed (${response.status})`);
  }
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
    throw await adminProblemMessage(response, `Admin publish draft failed (${response.status})`);
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
    throw await adminProblemMessage(response, `Admin validate draft failed (${response.status})`);
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
    throw await adminProblemMessage(response, `Admin draft diff failed (${response.status})`);
  }
  return (await response.json()) as AdminDefinitionDraftDiff;
}

export type AdminDefinitionEvaluationScenario = {
  scenarioId: string;
  scenarioVersion: number;
  title: string;
  prompt: string;
  requirementLevel: "Required" | "Advisory";
  checkType:
    | "ToolOffered"
    | "ToolNotOffered"
    | "ResourceBound"
    | "TriggerSchedulePermitted"
    | "ExternalActionDenied";
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
  draftId: string,
  signal?: AbortSignal
): Promise<AdminDefinitionEvaluationScenario[]> {
  const response = await ownerFetch(`/api/v2/admin/definition-drafts/${draftId}/evaluation-scenarios`, { signal });
  if (!response.ok) {
    throw await adminProblemMessage(response, `Admin evaluation scenarios failed (${response.status})`);
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
    throw await adminProblemMessage(response, `Admin upsert evaluation scenario failed (${response.status})`);
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
    throw await adminProblemMessage(response, `Admin remove evaluation scenario failed (${response.status})`);
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
    throw await adminProblemMessage(response, `Admin run evaluation scenario failed (${response.status})`);
  }
  return (await response.json()) as AdminDefinitionEvaluationResult;
}

export async function listAdminDefinitionEvaluationResults(
  draftId: string,
  signal?: AbortSignal
): Promise<AdminDefinitionEvaluationResult[]> {
  const response = await ownerFetch(`/api/v2/admin/definition-drafts/${draftId}/evaluation-results`, { signal });
  if (!response.ok) {
    throw await adminProblemMessage(response, `Admin evaluation results failed (${response.status})`);
  }
  return (await response.json()) as AdminDefinitionEvaluationResult[];
}

export async function listAdminDefinitionPublications(
  definitionId: string
): Promise<AdminDefinitionPublicationSummary[]> {
  const response = await ownerFetch(`/api/v2/admin/definitions/${encodeURIComponent(definitionId)}/publications`);
  if (!response.ok) {
    throw await adminProblemMessage(response, `Admin publications failed (${response.status})`);
  }
  const payload = (await response.json()) as { items: AdminDefinitionPublicationSummary[] };
  return payload.items;
}

export async function deprecateAdminDefinitionPublication(
  definitionId: string,
  version: number,
  expectedMetadataRevision: number
): Promise<AdminDefinitionPublicationSummary> {
  const response = await ownerFetch(
    `/api/v2/admin/definitions/${encodeURIComponent(definitionId)}/publications/${version}/deprecate`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ expectedMetadataRevision })
    }
  );
  if (!response.ok) {
    throw await adminProblemMessage(response, `Admin deprecate publication failed (${response.status})`);
  }
  return (await response.json()) as AdminDefinitionPublicationSummary;
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
  lifecycle: string;
  revision: number;
  personaRevision: number;
  persona?: { name: string; role: string; description: string; tone: string };
  unattendedModelCatalogKey?: string | null;
  unattendedReasoningEffort?: string | null;
};

export type AdminCreateInstancePersona = {
  name: string;
  role: string;
  description: string;
  tone: string;
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

const adminDetailMessages: Record<string, string> = {
  "Voice.Enabled requires speechRecognizer and speechSynthesizer aliases.":
    "Add a speech recognizer and a speech synthesizer before saving a voice-enabled draft.",
  "Text-only definitions must omit speech provider aliases.":
    "Remove the speech recognizer and speech synthesizer when voice is off.",
  "definitionId cannot change on update.":
    "The definition ID cannot be changed after the draft is created.",
  "definitionId must match the candidate definitionId.":
    "The definition ID does not match this draft.",
  "definitionId is required.":
    "Enter a definition ID using lowercase letters, digits, and hyphens.",
  "goals must be 1..10 nonempty strings of at most 500 characters.":
    "Add between 1 and 10 goals. Each goal needs text and can be at most 500 characters.",
  "identity field lengths are invalid.":
    "Check the name, role, tone, and description lengths, then try again.",
  "identity is required.":
    "Add a name, role, description, and tone.",
  "identity fields are required.":
    "Add a name, role, description, and tone.",
  "systemInstructions is required.":
    "Add system instructions.",
  "systemInstructions must be 1..8000 characters.":
    "System instructions must be between 1 and 8000 characters.",
  "language is required.":
    "Set the conversation language to auto or a tag such as en.",
  "maxOutputTokens must be 1..4096.":
    "Max output tokens must be between 1 and 4096.",
  "silenceThresholdMs is out of range.":
    "Silence threshold must be between 1,000 and 120,000 milliseconds.",
  "cooldownMs is out of range.":
    "Cooldown must be between 5,000 and 600,000 milliseconds.",
  "speakingRate must be 0.5..2.0.":
    "Speaking rate must be between 0.5 and 2.",
  "Draft revision is stale.":
    "This draft changed somewhere else. Reload it, then try again."
};

export function adminRequestErrorFromProblem(
  problem: { title?: string; detail?: string; diagnosticId?: unknown; extensions?: unknown },
  fallback: string
): AdminRequestError {
  const diagnosticId = typeof problem.diagnosticId === "string" && problem.diagnosticId.length > 0
    ? problem.diagnosticId
    : undefined;
  return new AdminRequestError(
    friendlyAdminDetail(problem.detail ?? problem.title, fallback),
    diagnosticId);
}

export class AdminRequestError extends Error {
  readonly diagnosticId?: string;

  constructor(message: string, diagnosticId?: string) {
    super(message);
    this.name = "AdminRequestError";
    this.diagnosticId = diagnosticId;
  }
}

export function friendlyAdminDetail(detail: string | undefined, fallback: string): string {
  const text = detail?.trim() ?? "";
  if (text.length === 0) {
    return fallback;
  }
  const known = adminDetailMessages[text];
  if (known) {
    return known;
  }
  if (text.startsWith("Definition candidate body is invalid")) {
    return "A field in this draft could not be read. Check numbers and Advanced JSON, then try again.";
  }
  const alias = text.match(/^(languageModel|speechRecognizer|speechSynthesizer) alias '([^']+)' is not configured\.$/);
  if (alias) {
    const label = alias[1] === "languageModel"
      ? "language model"
      : alias[1] === "speechRecognizer"
        ? "speech recognizer"
        : "speech synthesizer";
    return `The ${label} “${alias[2]}” is not available on this server. Choose one that is configured.`;
  }
  return text;
}

export async function adminProblemMessage(response: Response, fallback: string): Promise<AdminRequestError> {
  try {
    const problem = (await response.json()) as { title?: string; detail?: string; diagnosticId?: unknown };
    return adminRequestErrorFromProblem(problem, fallback);
  } catch {
    return new AdminRequestError(fallback);
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
    throw await adminProblemMessage(response, `Admin persona update failed (${response.status})`);
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
    throw await adminProblemMessage(response, `Admin lifecycle update failed (${response.status})`);
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
    throw await adminProblemMessage(response, `Admin version update failed (${response.status})`);
  }
  return (await response.json()) as AdminAgentInstance;
}

export async function listAdminDraftResources(draftId: string): Promise<AdminDefinitionDraftResource[]> {
  const response = await ownerFetch(`/api/v2/admin/definition-drafts/${draftId}/resources`);
  if (!response.ok) {
    throw await adminProblemMessage(response, `Admin draft resources failed (${response.status})`);
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
    throw await adminProblemMessage(response, `Admin resource upload failed (${response.status})`);
  }
  return (await response.json()) as AdminResourceContentStored;
}

export type AdminBindDraftResourceItem = {
  logicalPath: string;
  kind: string;
  mediaType: string;
  contentSha256: string;
  byteLength: number;
};

export async function bindAdminDraftResources(
  draftId: string,
  expectedRevision: number,
  items: AdminBindDraftResourceItem[]
): Promise<{ revision: number; items: AdminDefinitionDraftResource[] }> {
  const response = await ownerFetch(`/api/v2/admin/definition-drafts/${draftId}/resources/batch`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ expectedRevision, items })
  });
  if (!response.ok) {
    throw await adminProblemMessage(response, `Admin resource bind failed (${response.status})`);
  }
  return (await response.json()) as { revision: number; items: AdminDefinitionDraftResource[] };
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
    throw await adminProblemMessage(response, `Admin resource bind failed (${response.status})`);
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
    throw await adminProblemMessage(response, `Admin resource remove failed (${response.status})`);
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
    throw await adminProblemMessage(response, `Admin publication resources failed (${response.status})`);
  }
  const payload = (await response.json()) as { items: AdminDefinitionPublicationResource[] };
  return payload.items;
}

export async function createAdminAgentInstance(
  definitionId: string,
  version: number,
  persona?: AdminCreateInstancePersona | null
): Promise<AdminAgentInstance> {
  const body: {
    definitionId: string;
    version: number;
    persona?: AdminCreateInstancePersona;
  } = { definitionId, version };
  if (persona) {
    body.persona = persona;
  }
  const response = await ownerFetch("/api/v2/admin/agent-instances", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body)
  });
  if (!response.ok) {
    throw await adminProblemMessage(response, `Admin create instance failed (${response.status})`);
  }
  return (await response.json()) as AdminAgentInstance;
}

export type AdminLearnedMemoryScope = "Session" | "IdentityUser" | "User";

export type AdminLearnedMemoryItem = {
  memoryId: string;
  status?: "Active" | "Superseded" | "Deleted";
  kind: string;
  subject: string;
  content: string;
  provenance: {
    source: string;
    originSessionId: string | null;
    originMemoryId: string | null;
    recordedAt: string;
    derivedFromMemoryIds?: string[];
    maintenanceOrigin?: string | null;
    maintenanceAgentInstanceId?: string | null;
    maintenanceSessionId?: string | null;
    maintenanceAgentRunId?: string | null;
  };
  updatedAt: string;
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
    throw await adminProblemMessage(response, `Admin learned memory list failed (${response.status})`);
  }
  const payload = (await response.json()) as { items: AdminLearnedMemoryItem[] };
  return payload.items;
}

export async function getAdminLearnedMemory(instanceId: string, memoryId: string, scope: AdminLearnedMemoryScope, sessionId?: string): Promise<AdminLearnedMemoryItem> {
  const params = new URLSearchParams({ scope });
  if (sessionId) params.set("sessionId", sessionId);
  const response = await ownerFetch(`/api/v2/admin/agent-instances/${instanceId}/learned-memory/${memoryId}?${params}`);
  if (!response.ok) throw await adminProblemMessage(response, "Unable to inspect memory sources.");
  return response.json() as Promise<AdminLearnedMemoryItem>;
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
    throw await adminProblemMessage(response, `Admin learned memory delete failed (${response.status})`);
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
    throw await adminProblemMessage(response, `Admin learned memory reset failed (${response.status})`);
  }
  const payload = (await response.json()) as { itemsRemoved: number };
  return payload.itemsRemoved;
}

export type SystemCredential = {
  credentialId: string; displayName: string; kind: string; status: string;
  metadata: Record<string, string>; allowedOrigins: string[]; revision: number;
  createdAtUtc: string; updatedAtUtc: string; bindingCount: number;
};
export type CredentialBinding = { bindingId: string; credentialId: string; reference: string; revision: number; credential: SystemCredential };
export type CredentialInput = { displayName: string; kind: string; metadata: Record<string, string>; allowedOrigins: string[]; protectedValue: string };
async function credentialRequest<T>(path: string, method = "GET", body?: unknown): Promise<T> {
  const response = await ownerFetch(`/api/v2/admin/${path}`, { method,
    headers: body ? { "Content-Type": "application/json" } : undefined, body: body ? JSON.stringify(body) : undefined });
  if (!response.ok) throw await adminProblemMessage(response, `Credential operation failed (${response.status})`);
  return response.status === 204 ? undefined as T : await response.json() as T;
}
export const listCredentials = async () => (await credentialRequest<{ items: SystemCredential[] }>("credentials")).items;
export const createCredential = (input: CredentialInput) => credentialRequest<SystemCredential>("credentials", "POST", input);
export const updateCredential = (c: SystemCredential, input: Pick<SystemCredential, "displayName" | "status" | "metadata" | "allowedOrigins">) =>
  credentialRequest<SystemCredential>(`credentials/${c.credentialId}`, "PATCH", { ...input, expectedRevision: c.revision });
export const replaceCredentialValue = (c: SystemCredential, protectedValue: string) =>
  credentialRequest<SystemCredential>(`credentials/${c.credentialId}/value`, "PUT", { expectedRevision: c.revision, protectedValue });
export const deleteCredential = (c: SystemCredential) => credentialRequest<void>(`credentials/${c.credentialId}?expectedRevision=${c.revision}`, "DELETE");
export const listCredentialBindings = async (id: string) => (await credentialRequest<{ items: CredentialBinding[] }>(`agent-instances/${id}/credential-bindings`)).items;
export const bindCredential = (id: string, credentialId: string, reference: string, expectedInstanceRevision: number) =>
  credentialRequest<CredentialBinding>(`agent-instances/${id}/credential-bindings`, "POST", { credentialId, reference, expectedInstanceRevision });
export const unbindCredential = (id: string, b: CredentialBinding, instanceRevision: number) =>
  credentialRequest<void>(`agent-instances/${id}/credential-bindings/${b.bindingId}?expectedRevision=${b.revision}&expectedInstanceRevision=${instanceRevision}`, "DELETE");
export const resetBrowserProfile = (id: string, expectedInstanceRevision: number) =>
  credentialRequest<void>(`agent-instances/${id}/browser-profile/reset`, "POST", { expectedInstanceRevision, confirm: true });

export type BrowserPrivacyMode = "Protected" | "Unmasked" | "Disabled";
export type BrowserScreenshotPolicy = { mode: BrowserPrivacyMode; unmaskedOrigins: string[]; trustedGraphicsOrigins: string[]; revision: number };
export type BrowserPrivacy = {
  saved: BrowserScreenshotPolicy; effective: BrowserScreenshotPolicy;
  deployment: { captureAllowed: boolean; unmaskedAllowed: boolean; unmaskedOriginCeiling: string[]; graphicsOriginCeiling: string[] };
  restartRequired: boolean; activation: string; durable: boolean;
};
export const getBrowserPrivacy = (signal?: AbortSignal) => browserPrivacyRequest("GET", undefined, signal);
export const saveBrowserPrivacy = (input: Omit<BrowserScreenshotPolicy, "revision"> & { expectedRevision: number; acknowledgeExposure: boolean }, signal?: AbortSignal) =>
  browserPrivacyRequest("PUT", input, signal);
async function browserPrivacyRequest(method: string, input?: unknown, signal?: AbortSignal): Promise<BrowserPrivacy> {
  const response = await ownerFetch("/api/v2/admin/browser/privacy", { method, signal,
    ...(input ? { headers: { "Content-Type": "application/json" }, body: JSON.stringify(input) } : {}) });
  if (!response.ok) throw await adminProblemMessage(response, "Unable to manage browser screenshot privacy.");
  return response.json() as Promise<BrowserPrivacy>;
}

export type AdminWebhookEvent = {
  eventId: string; displayName: string; eventKey: string; status: string; revision: number;
  createdAt: string; updatedAt: string; subscriberCount: number; activeSubscriberCount: number; lastReceivedAt: string | null;
};
export type AdminWebhookEventCredential = { eventId: string; eventKey: string; token: string; status: string };
export type AdminWebhookEventDetails = {
  event: AdminWebhookEvent;
  subscribers: { automationId: string; name: string; agentInstanceId: string; status: string }[];
  signals: { receiptId: string; sourceEventId: string; receivedAt: string }[];
  deliveries: { receiptId: string; sourceEventId: string; receivedAt: string; automationId: string; agentInstanceId: string; status: string }[];
};
const eventResourcePath = "connections/events";
export async function listWebhookEvents(): Promise<AdminWebhookEvent[]> {
  return (await credentialRequest<{ items: AdminWebhookEvent[] }>(eventResourcePath)).items;
}
export const getWebhookEvent = (eventId: string) => credentialRequest<AdminWebhookEventDetails>(`${eventResourcePath}/${eventId}`);
export const createWebhookEvent = (displayName: string, eventKey: string) =>
  credentialRequest<AdminWebhookEventCredential>(eventResourcePath, "POST", { displayName, eventKey });
export const renameWebhookEvent = (eventId: string, displayName: string, expectedRevision: number) =>
  credentialRequest<AdminWebhookEvent>(`${eventResourcePath}/${eventId}`, "PUT", { displayName, expectedRevision });
export const rotateWebhookEvent = (eventId: string) => credentialRequest<AdminWebhookEventCredential>(`${eventResourcePath}/${eventId}/rotate`, "POST");
export const revokeWebhookEvent = (eventId: string) => credentialRequest<AdminWebhookEvent>(`${eventResourcePath}/${eventId}/revoke`, "POST");

export async function setAdminUnattendedModel(
  instanceId: string,
  expectedRevision: number,
  catalogKey: string | null,
  reasoningEffort: string | null
): Promise<AdminAgentInstance> {
  const response = await ownerFetch(`/api/v2/admin/agent-instances/${instanceId}/unattended-model`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ expectedRevision, catalogKey, reasoningEffort })
  });
  if (!response.ok) {
    throw await adminProblemMessage(response, `Unattended model update failed (${response.status})`);
  }
  return (await response.json()) as AdminAgentInstance;
}

export type HarnessMode = "Disabled" | "Assisted" | "Managed";
export type HarnessScope = "KnowledgeResources" | "Instructions" | "ToolSelection";
export type HarnessPolicy = { mode: HarnessMode; scopes: HarnessScope[]; sources: string[]; eligibleTools: string[]; frozen: boolean };
export type HarnessSkill = { id: string; name: string; description: string; procedure: string; requiredCapabilities: string[]; resourcePaths: string[] };
export type HarnessOperation = { kind: string; draftRevision: number; id: string | null; content: string | null; source: string | null;
  enabled: boolean | null; allowUnreadUnsupportedTypes: boolean | null };
export type HarnessApproval = { approvalId: string; actionHash: string; operation: HarnessOperation; status: string };
export type HarnessEvidence = { actor: string; draftRevision: number; check: string; status: string; expected: string; observed: string; limitation: string | null };
export type HarnessReview = {
  instanceId: string; instanceRevision: number; activeVersion: number; policy: HarnessPolicy; policyRevision: number;
  preparation: { preparationId: string; draftId: string; baseVersion: number; purpose: string; status: string;
    approvals: HarnessApproval[]; evidence: HarnessEvidence[]; publishedVersion: number | null; diagnosticId: string | null; publishedDraftRevision: number | null } | null;
  draftRevision: number | null; instructions: string | null; skills: HarnessSkill[];
  knowledge: { identity: string; title: string; citation: string; resourcePath: string | null }[]; selectedTools: string[];
  diff: AdminDefinitionDraftDiff | null; resources: AdminDefinitionDraftResource[];
};

export async function getHarnessReview(instanceId: string): Promise<HarnessReview> {
  const response = await ownerFetch(`/api/v2/admin/agent-instances/${instanceId}/harness`);
  if (!response.ok) throw await adminProblemMessage(response, "Harness management could not be loaded.");
  return await response.json() as HarnessReview;
}

export async function updateHarness(instanceId: string, action: string, body: unknown, signal?: AbortSignal): Promise<HarnessReview> {
  const response = await ownerFetch(`/api/v2/admin/agent-instances/${instanceId}/harness/${action}`, {
    method: action === "policy" ? "PUT" : "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body), ...(signal ? { signal } : {})
  });
  if (!response.ok) throw await adminProblemMessage(response, "Harness operation failed. Reload the candidate before retrying.");
  return await response.json() as HarnessReview;
}

export type ExperienceContent = {
  goal: string; attempts: string[]; decisions: string[]; outcomes: string[]; corrections: string[];
  unresolved: string[]; difficulties: string[]; lessons: string[];
};
export type ExperienceItem = {
  experienceId: string; sourceKind: "Session" | "AgentRun" | "Consolidation"; sourceId: string; throughCursor: number;
  sourceAt: string; sourceCreatedAt?: string; checkpointAt?: string | null; definitionId: string; definitionVersion: number; generationAgentRunId: string;
  modelKey: string; status: string; visibility: "Eligible" | "Suppressed" | "Superseded" | "Deleted"; revision: number;
  derivedFromExperienceIds?: string[]; maintenanceOrigin?: string | null;
  eligibleForContext: boolean; content: ExperienceContent | null; diagnosticId: string | null; failureSummary: string | null;
};
export type IdentityMaintenanceSettings = { agentInstanceId: string; allowAgentConsolidation: boolean; revision: number };
export type ExperienceReview = { enabled: boolean; settingsRevision: number; contextBudgetCharacters: number; items: ExperienceItem[] };
export async function instanceContinuityRequest<T>(instanceId: string, path: string, method = "GET", body?: unknown): Promise<T> {
  const response = await ownerFetch(`/api/v2/admin/agent-instances/${instanceId}/${path}`, {
    method, ...(body === undefined ? {} : { headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) })
  });
  if (!response.ok) throw await adminProblemMessage(response, "The instance update could not be completed.");
  return await response.json() as T;
}

export type ScheduleTiming = {
  kind: "oneShot" | "fixedInterval" | "daily" | "weekly"; timeZone: string; atUtc?: string | null;
  interval: number; localTime?: string | null; weekdays?: number[] | null; anchorAtUtc?: string | null;
  endAtUtc?: string | null; startDate?: string | null; endDate?: string | null; maxOccurrences?: number | null;
};
export type EventDispatch = { mode: "everyMatch" | "coalesceLatest"; windowSeconds?: number | null };
export type EventFilter = { filterExpression?: string | null; dispatch?: EventDispatch | null };
export type AutomationTrigger = { kind: "schedule"; schedule: ScheduleTiming } | ({ kind: "event"; eventId: string } & EventFilter) | ({ kind: "coreEvent"; coreEventKey: string } & EventFilter);
export type CoreEventType = { key: string; eligible: boolean; reason: string | null; example: Record<string, unknown> };
export type AutomationPreset = { presetId: string; presetVersion: number; name: string; description: string; instructions: string; trigger: AutomationTrigger; eligible: boolean; prerequisites: string[] };
export type FilterTestResult = { matched: boolean | null; status: "matched" | "notMatched" | "error"; code?: string | null };
export type AutomationDraft = { presetId?: string | null; presetVersion?: number | null; requiresTools?: boolean; requiresVision?: boolean; executionTarget: AutomationTarget; completionDelivery: AutomationDelivery; expectedRevision: number; enabled: boolean; name: string; instructions: string; trigger: AutomationTrigger;
  modelKey: string | null; reasoningEffort: string | null };
export type Automation = { presetId?: string | null; presetVersion?: number | null; requiresTools?: boolean; requiresVision?: boolean; executionTarget: AutomationTarget; completionDelivery: AutomationDelivery; suspensionReason?: string | null; automationId: string; revision: number; name: string; instructions: string; enabled: boolean; status: string;
  trigger: AutomationTrigger; authorizationOrigin: string; sourceSessionId: string | null; sourceEventId: string | null;
  createdAt: string; nextRunAt: string | null; modelKey: string | null; reasoningEffort: string | null;
  effectiveModelKey: string | null; lastAgentRunId: string | null; executionStatus: string | null; outcome: string | null };
export type AutomationPolicy = { allowOneShot: boolean; allowDaily: boolean; allowWeekly: boolean; allowFixedInterval: boolean;
  allowIndefiniteRecurrence: boolean; oneShotHorizonDays: number; minRecurrenceDays: number; minFixedIntervalSeconds: number; maxActiveRegistrations: number; allowEvents?: boolean; allowCoreEvents?: boolean };
export type AutomationReview = { items: Automation[]; policy?: AutomationPolicy | null };

export type ExecutionBudgetProfile = { maxSteps: number; durationSeconds: number; perToolSeconds: number; preset: number };
export type ExecutionBudgetPolicy = Partial<Record<'standard' | 'interactiveBrowser' | 'unattendedBoundBrowser', ExecutionBudgetProfile | null>>;
export async function getExecutionBudgets(instanceId: string): Promise<{ revision: number; executionBudgets: ExecutionBudgetPolicy | null; definitionDefaults: ExecutionBudgetPolicy | null }> {
  const response = await ownerFetch(`/api/v2/admin/agent-instances/${instanceId}/execution-budgets`);
  if (!response.ok) throw new Error('Execution budgets could not be loaded. Retry the read.');
  return response.json();
}
export async function setExecutionBudgets(instanceId: string, expectedRevision: number, executionBudgets: ExecutionBudgetPolicy): Promise<{ revision: number }> {
  const response = await ownerFetch(`/api/v2/admin/agent-instances/${instanceId}/execution-budgets`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ expectedRevision, executionBudgets })
  });
  if (!response.ok) throw new Error(response.status === 409 ? 'The Instance changed. Reload and review your changes before saving.' : 'Execution budgets could not be saved. Check the host limits and retry.');
  return response.json();
}

export type ExecutionBudgetLimits = { maxSteps: number; durationSeconds: number; perToolSeconds: number };
export async function getExecutionBudgetLimits(): Promise<ExecutionBudgetLimits> {
  const response = await ownerFetch('/api/v2/admin/execution-budget-limits');
  if (!response.ok) throw new Error('Execution limits could not be loaded.');
  const value: ExecutionBudgetLimits = await response.json();
  if (!value || !Number.isSafeInteger(value.maxSteps) || value.maxSteps < 8
    || !Number.isSafeInteger(value.durationSeconds) || value.durationSeconds < 60
    || !Number.isSafeInteger(value.perToolSeconds) || value.perToolSeconds < 1
    || value.perToolSeconds > value.durationSeconds) throw new Error('Execution limits response is invalid.');
  return value;
}
