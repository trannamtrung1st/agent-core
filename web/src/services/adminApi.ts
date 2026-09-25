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
