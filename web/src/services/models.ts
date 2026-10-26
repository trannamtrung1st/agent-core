import { identityRequest, tenantApi } from './identity';

export const modelKinds = ['OpenAI', 'Anthropic', 'Google', 'OpenRouter', 'OpenAICompatible'] as const;
export type ModelKind = typeof modelKinds[number];
export type ModelTransport = 'Responses' | 'AnthropicMessages' | 'GeminiContent' | 'ChatCompletions';
export type ModelCapabilities = {
  tools: boolean; vision: boolean; hierarchy: 'Unsupported' | 'NativeDeveloper' | 'OrderedTrustedSections';
  semanticMode: 'NativeStructuredOutput' | 'ResponseFunction'; structuredOutput: boolean; forcedToolChoice: boolean;
  reasoningEfforts: string[]; defaultReasoningEffort: string | null;
};
export type ModelCapabilityMetadata = ModelCapabilities & { source: string; verifiedAt: string | null };
export type ModelPreset = { presetId: string; kind: ModelKind; externalModelId: string; label: string; transport: ModelTransport;
  capabilities: ModelCapabilityMetadata; enabled: boolean; archived: boolean; revision: number; offeringReferences?: number | null };
export type ModelConnection = { connectionId: string; tenantId: string; kind: ModelKind; label: string; endpoint: string;
  credentialId: string | null; noCredential: boolean; enabled: boolean; revision: number; createdAt: string; updatedAt: string;
  lastTestAt: string | null; lastTestResult: string | null };
export type ModelOffering = { offeringId: string; tenantId: string; connectionId: string; kind: ModelKind; presetId: string | null;
  externalModelId: string; label: string; transport: ModelTransport; capabilities: ModelCapabilityMetadata;
  enabled: boolean; retired: boolean; revision: number; observation: 'Unknown' | 'Observed' | 'NotReturned'; lastSeenAt: string | null };
export type AutoRoutingPolicy = { enabled: boolean; allowConfiguredDefault: boolean; minimumProbability: number };
export type ModelSettings = { routing?: AutoRoutingPolicy | null; tenantId: string; defaultOfferingId: string | null; defaultReasoningEffort: string | null; revision: number };
export type ModelAdmin = { connections: ModelConnection[]; offerings: ModelOffering[]; presets: ModelPreset[]; settings: ModelSettings;
  securityRevision: number; access: { canCreateConnection: boolean; canCreateOffering: boolean; canManageDefault: boolean; manageConnectionIds: string[]; testConnectionIds: string[]; manageOfferingIds: string[] }; availability: { offeringId: string; available: boolean; reason: string }[] };
export type MetadataTest = { metadataAvailable: boolean; result: string; modelsObserved: number; testedAt: string };
export const modelApi = (tenant: string, suffix = '') => tenantApi(tenant, suffix);
export const kindLabel = (kind: ModelKind) => ({ OpenAI: 'OpenAI', Anthropic: 'Anthropic', Google: 'Google Gemini', OpenRouter: 'OpenRouter', OpenAICompatible: 'Custom compatible endpoint' })[kind];
export const transportsFor = (kind: ModelKind): ModelTransport[] => ({ OpenAI: ['Responses'], Anthropic: ['AnthropicMessages'], Google: ['GeminiContent'], OpenRouter: ['ChatCompletions'], OpenAICompatible: ['ChatCompletions', 'Responses'] } as Record<ModelKind, ModelTransport[]>)[kind];
export const blankCapabilities = (): ModelCapabilities => ({ tools: false, vision: false, hierarchy: 'Unsupported', semanticMode: 'NativeStructuredOutput', structuredOutput: false, forcedToolChoice: false, reasoningEfforts: [], defaultReasoningEffort: null });
export const capabilityInput = (c: ModelCapabilities): ModelCapabilities => ({ tools: c.tools, vision: c.vision, hierarchy: c.hierarchy,
  semanticMode: c.semanticMode, structuredOutput: c.structuredOutput, forcedToolChoice: c.forcedToolChoice,
  reasoningEfforts: [...c.reasoningEfforts], defaultReasoningEffort: c.defaultReasoningEffort });
export const offeringLabel = (offering: ModelOffering, catalog: ModelAdmin) => `${offering.label} · ${catalog.connections.find(c => c.connectionId === offering.connectionId)?.label ?? 'Connection unavailable'}`;
export const availabilityLabel = (reason: string) => ({ available: 'Available for admission', retired: 'Retired', disabled: 'Disabled', connection_unavailable: 'Connection disabled or unavailable', credential_unavailable: 'Credential unavailable', not_returned: 'Not returned by latest discovery', capabilities_unverified: 'Required capabilities not declared' } as Record<string, string>)[reason] ?? 'Availability unknown';
export async function readModels(tenant: string, signal?: AbortSignal) { return identityRequest<ModelAdmin>(modelApi(tenant, '/models'), { signal }); }

export const capabilitySummary = (c: ModelCapabilities) => `Tools: ${c.tools ? 'yes' : 'no'} · Images: ${c.vision ? 'yes' : 'no'} · Hierarchy: ${c.hierarchy} · Semantic output: ${c.semanticMode} · Native schema: ${c.structuredOutput ? 'yes' : 'no'} · Forced tools: ${c.forcedToolChoice ? 'yes' : 'no'} · Efforts: ${c.reasoningEfforts.join(', ') || 'none'} · Default effort: ${c.defaultReasoningEffort ?? 'none'}`;

export const modelReferenceKinds = [
  { value: 'default', label: 'Organization default' }, { value: 'definitions', label: 'Definitions' },
  { value: 'instances', label: 'Instances' }, { value: 'automations', label: 'Automations' },
  { value: 'sessions', label: 'Conversations' }, { value: 'runs', label: 'Runs' }
] as const;
export type ModelReferenceKind = typeof modelReferenceKinds[number]['value'];
export type ModelReferenceTarget = { kind: 'connection' | 'offering'; id: string; label: string };
export type ModelReference = { resourceId: string; referenceId: string; offeringId: string; label: string; usage: string; revision: number; state: string };
export type ModelReferencePage = { items: ModelReference[]; total: number; page: number; pageSize: number; securityRevision: number };
export const modelReferencesApi = (tenant: string, target: ModelReferenceTarget, kind: ModelReferenceKind, page = 0) => modelApi(tenant,
  `/${target.kind === 'connection' ? 'model-connections' : 'models'}/${encodeURIComponent(target.id)}/references?kind=${kind}&page=${page}&pageSize=20`);
export async function modelReferenceImpact(tenant: string, target: ModelReferenceTarget, expectedRevision: number, signal: AbortSignal) {
  const rows = await Promise.all(modelReferenceKinds.map(async kind => ({ ...kind,
    page: await identityRequest<ModelReferencePage>(modelReferencesApi(tenant, target, kind.value), { signal }) })));
  if (rows.some(row => row.page.securityRevision !== expectedRevision)) throw new Error('Model references changed. Close this preview and reload current configuration before saving.');
  return rows;
}

export const capabilityFilters = [{ text: 'Tools', value: 'tools' }, { text: 'Images', value: 'vision' }, { text: 'Reasoning controls', value: 'reasoning' }, { text: 'Native schema', value: 'schema' }];
export const supportsCapabilityFilter = (c: ModelCapabilities, value: unknown) => value === 'tools' ? c.tools : value === 'vision' ? c.vision : value === 'reasoning' ? c.reasoningEfforts.length > 0 : value === 'schema' && c.structuredOutput;
export const capabilityLabels = (c: ModelCapabilities) => [c.tools && 'Tools', c.vision && 'Images', c.reasoningEfforts.length > 0 && 'Reasoning', c.structuredOutput && 'Native schema'].filter(Boolean).join(' · ') || 'None declared';
