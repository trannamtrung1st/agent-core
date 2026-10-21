import type { AutomationDelivery,AutomationTarget } from "./api";

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

export type AdminCapabilityDescriptor = { name: string; category: string; summary: string; tags: string[]; discoverable: boolean; defaultProjectionClass: string; configured: boolean };
export type AdminToolRegistry = { toolNames: string[]; maxToolAllowlistEntries: number | null; capabilities?: AdminCapabilityDescriptor[] };

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
  "agentInstructions is required.":
    "Add agent instructions.",
  "agentInstructions must be 1..8000 characters.":
    "Agent instructions must be between 1 and 8000 characters.",
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

export type AdminBindDraftResourceItem = {
  logicalPath: string;
  kind: string;
  mediaType: string;
  contentSha256: string;
  byteLength: number;
};

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

export type SystemCredential = {
  credentialId: string; displayName: string; kind: string; status: string;
  metadata: Record<string, string>; allowedOrigins: string[]; revision: number;
  createdAtUtc: string; updatedAtUtc: string; bindingCount: number; purpose?: "General" | "EmailAccount" | "GoogleDriveAccount" | "ContainerRegistry"; emailAccount?: EmailAccountProfile | null; driveAccount?: import("./drive").DriveProfile | null; containerRegistry?:ContainerRegistryProfile|null;
};
export type CredentialBinding = { bindingId: string; credentialId: string; reference: string; revision: number; credential: SystemCredential };
export type CredentialInput = { displayName: string; kind: string; metadata: Record<string, string>; allowedOrigins: string[]; protectedValue: string };

export type AdminWebhookEvent = {
  eventId: string; displayName: string; eventKey: string; status: string; revision: number;
  createdAt: string; updatedAt: string; subscriberCount: number; activeSubscriberCount: number; lastReceivedAt: string | null;
};
export type AdminWebhookEventCredential = { eventId: string; eventKey: string; token: string; status: string };
export type AdminWebhookEventDetails = {
  event: AdminWebhookEvent;
  subscribers: { automationId: string; name: string; agentInstanceId: string; status: string }[];
  signals: { receiptId: string; sourceEventId: string; receivedAt: string }[];
  deliveries: { triggerId?: string; receiptId: string; sourceEventId: string; receivedAt: string; automationId: string; agentInstanceId: string; status: string }[];
};

export type HarnessMode = "Disabled" | "Assisted" | "Managed";
export type HarnessScope = "KnowledgeResources" | "Instructions" | "ToolSelection" | "Settings" | "Skills" | "Resources" | "Connections" | "ManagementPolicy" | "AutomationPreferences";
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
  instanceChanges?: { operationId: string; operation: string; instanceRevision: number; definitionVersion: number; policyRevision: number; approvalId: string | null; actionHash: string; source: string | null; savedAt: string; evidence: HarnessEvidence }[];
};

export type ExperienceContent = {
  goal: string; attempts: string[]; decisions: string[]; outcomes: string[]; corrections: string[];
  unresolved: string[]; difficulties: string[]; lessons: string[];
};
export type ExperienceItem = {
  experienceId: string; sourceKind: "Session" | "AgentRun" | "Consolidation"; sourceId: string; throughCursor: number;
  sourceRunOutputContract?: "ConversationResponse" | "BackgroundOutcome" | "CompletionReport" | null;
  sourceAt: string; sourceCreatedAt?: string; checkpointAt?: string | null; definitionId: string; definitionVersion: number; generationAgentRunId: string;
  modelKey: string; status: string; visibility: "Eligible" | "Suppressed" | "Superseded" | "Deleted"; revision: number;
  derivedFromExperienceIds?: string[]; maintenanceOrigin?: string | null;
  eligibleForContext: boolean; content: ExperienceContent | null; diagnosticId: string | null; failureSummary: string | null;
};
export type IdentityMaintenanceSettings = { agentInstanceId: string; allowAgentConsolidation: boolean; revision: number };
export type ExperienceReview = { enabled: boolean; settingsRevision: number; contextBudgetCharacters: number; items: ExperienceItem[] };

export type ScheduleTiming = {
  kind: "oneShot" | "fixedInterval" | "daily" | "weekly"; timeZone: string; atUtc?: string | null;
  interval: number; localTime?: string | null; weekdays?: number[] | null; anchorAtUtc?: string | null;
  endAtUtc?: string | null; startDate?: string | null; endDate?: string | null; maxOccurrences?: number | null;
};
export type EventDispatch = { mode: "everyMatch" | "coalesceLatest"; windowSeconds?: number | null };
export type EventFilter = { filterExpression?: string | null; dispatch?: EventDispatch | null };
export type AutomationTrigger = { kind: "schedule"; schedule: ScheduleTiming } | ({ kind: "event"; eventId: string } & EventFilter) | ({ kind: "coreEvent"; coreEventKey: string } & EventFilter);
export type EventSourceReference = { kind: "builtin"; key: string } | { kind: "webhook"; eventId: string };
export type AutomationChild = ({ kind: "schedule"; schedule: ScheduleTiming; source?: never } | ({ kind: "event"; source: EventSourceReference } & EventFilter)) & { triggerId: string; enabled: boolean; revision: number; eligible?: boolean | null; eligibilityReason?: string | null };
export type EventCatalogEntry = { source: EventSourceReference; name: string; key: string; description: string; state: string;
  schemaVersion: number; example: Record<string, unknown>; fieldSchema: Record<string, unknown>; webhook?: AdminWebhookEvent | null };
export const eventSourceKey = (source: EventSourceReference) => source.kind === "builtin" ? `builtin:${source.key}` : `webhook:${source.eventId}`;
export type CoreEventType = { key: string; eligible: boolean; reason: string | null; example: Record<string, unknown> };
export type AutomationPreset = { presetId: string; presetVersion: number; name: string; description: string; instructions: string; trigger: AutomationTrigger; eligible: boolean; prerequisites: string[] };
export type FilterTestResult = { matched: boolean | null; status: "matched" | "notMatched" | "error"; code?: string | null };
export type AutomationDraft = { presetId?: string | null; presetVersion?: number | null; requiresTools?: boolean; requiresVision?: boolean; executionTarget: AutomationTarget; completionDelivery: AutomationDelivery; expectedRevision: number; enabled: boolean; name: string; instructions: string; triggers: AutomationChild[];
  modelKey: string | null; reasoningEffort: string | null };
export type Automation = { presetId?: string | null; presetVersion?: number | null; requiresTools?: boolean; requiresVision?: boolean; executionTarget: AutomationTarget; completionDelivery: AutomationDelivery; suspensionReason?: string | null; automationId: string; revision: number; name: string; instructions: string; enabled: boolean; status: string;
  triggers: AutomationChild[]; authorizationOrigin: string; sourceSessionId: string | null; sourceEventId: string | null;
  createdAt: string; nextRunAt: string | null; modelKey: string | null; reasoningEffort: string | null;
  effectiveModelKey: string | null; lastAgentRunId: string | null; executionStatus: string | null; outcome: string | null };
export type AutomationPolicy = { allowOneShot: boolean; allowDaily: boolean; allowWeekly: boolean; allowFixedInterval: boolean;
  allowIndefiniteRecurrence: boolean; oneShotHorizonDays: number; minRecurrenceDays: number; minFixedIntervalSeconds: number; maxActiveRegistrations: number; allowEvents?: boolean; allowCoreEvents?: boolean };
export type AutomationReview = { items: Automation[]; policy?: AutomationPolicy | null };

export type ExecutionBudgetProfile = { maxSteps: number; durationSeconds: number; perToolSeconds: number; preset: number; sandboxCommandSeconds?: number };
export type ExecutionBudgetPolicy = Partial<Record<'standard' | 'interactiveBrowser' | 'unattendedBoundBrowser', ExecutionBudgetProfile | null>>;

export type ExecutionBudgetLimits = { maxSteps: number; durationSeconds: number; perToolSeconds: number; sandboxCommandSeconds?: number };

export type ManagementAutoApply = 'ConversationPreferences' | 'TextResources' | 'SkillChanges';
export type ManagementOverrides = { mode: { value: HarnessMode } | null; scopes: { value: HarnessScope[] } | null;
  autoApply: { value: ManagementAutoApply[] } | null; configurationAssistance: { value: boolean } | null; harnessAuthoring: { value: boolean } | null };
export type SelfManagementPolicy = HarnessPolicy & { managedAutoApply: ManagementAutoApply[] | null;
  builtInSkills: { configurationAssistance: boolean; harnessAuthoring: boolean } | null };
export type SelfManagementView = { instanceRevision: number; policyRevision: number; source: string;
  effective: SelfManagementPolicy; definitionDefaults: SelfManagementPolicy; overrides: ManagementOverrides | null };

export type MailEndpoint = { host: string; port: number; tlsMode: 'ImplicitTls' | 'StartTls' };
export type EmailAccountProfile = { preset: 'Gmail' | 'Custom'; emailAddress: string; imapUsername: string; smtpUsername: string; imapEndpoint: MailEndpoint | null; smtpEndpoint: MailEndpoint | null;
  authMethod?: 'Password' | 'GoogleOAuth2'; googleSubject?: string | null; grantedScopes?: string[] | null;
  connectionStatus?: 'NotConnected' | 'Connected' | 'ReconnectRequired' | 'Disconnected'; authEpoch?: number; connectedAtUtc?: string | null; synthetic?: boolean };
export type EmailConnectionTest = { protocol: 'imap' | 'smtp'; status: 'connected' | 'failed' | 'synthetic' | 'disabled'; observedAtUtc: string; error: string | null };

export type EmailOAuthConnection = { status: string; configured: boolean; synthetic: boolean; error: string | null; lastCheckedAtUtc?: string | null; providerRevoked?: boolean | null; diagnosticId?: string | null };


export type EmailMonitoring = {
  accountId: string; enabled: boolean; intervalSeconds: number; configurationRevision: number;
  status: 'Off' | 'HostDisabled' | 'Paused' | 'Initializing' | 'Checking' | 'Retrying' | 'Gap' | 'Monitoring';
  hostAvailable: boolean; accountAvailable: boolean; subscribedInstances: number; checkPending: boolean;
  lastCheckedAtUtc: string | null; lastSuccessAtUtc: string | null; lastEventAtUtc: string | null;
  nextCheckAtUtc: string | null; lastGapAtUtc: string | null; errorCode: string | null; diagnosticId: string | null;
};

export type ContainerRegistryProfile = {registry:string; username:string; repositoryPrefixes:string[]; expiresAtUtc?:string|null};
