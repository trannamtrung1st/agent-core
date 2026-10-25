import type { AutomationPolicy, ScheduleTiming, EventSourceReference, EventDispatch } from './adminApi';
import type { TenantAgentEditor } from './tenantAgentConfiguration';
import { tenantAgentApi } from './tenantAgentConfiguration';
import { IdentityCompletionError, identityRequest, tenantApi } from './identity';
import type { SessionPickerSource, SessionPickerItem } from './api';

export type TenantAutomation = {
  presetId?: string | null; presetVersion?: number | null;
  automationId: string; name: string; instructions: string; status: string; revision: number; triggerRevision: number;
  nextOccurrenceAtUtc: string | null; occurrenceCount: number; suspensionReason: string | null;
  modelOverrideOfferingId: string | null; modelOverrideReasoningEffort: string | null; requiresTools: boolean; requiresVision: boolean;
  executionTarget: string; completionDelivery: string; reportToSessionId?: string | null; targetSessionId?: string | null; createdAt: string; updatedAt: string;
  triggers: { kind: string; schedule?: ScheduleTiming; source?: EventSourceReference; triggerId?: string; enabled?: boolean; revision?: number; filterExpression?: string | null; dispatch?: EventDispatch | null }[];
};
export type TenantAutomationItem = { automation: TenantAutomation; canUpdate: boolean; canCancel: boolean; canRun: boolean };
export type TenantAutomationEditor = Pick<TenantAgentEditor, 'agent' | 'securityRevision'> & {
  presets?: TenantAutomationPreset[];
  policy: AutomationPolicy | null; items: TenantAutomationItem[]; total: number; page: number; pageSize: number; canCreate: boolean; canSchedule: boolean; canCoreEvents?: boolean; canWebhookEvents?: boolean;
};
export type TenantAutomationPreset = { presetId: string; presetVersion: number; name: string; description: string; instructions: string;
  triggerKind: 'schedule' | 'coreEvent'; coreEventKey: string | null; filterExpression: string | null; dispatch: EventDispatch | null; eligible: boolean; prerequisites: string[] };
export type TenantAutomationEvent = { localKey: string; triggerId?: string; revision: number; enabled: boolean; source?: EventSourceReference; filterExpression: string | null; dispatch: EventDispatch };
export const newTenantAutomationEvent = (): TenantAutomationEvent => ({ localKey: crypto.randomUUID(), revision: 0, enabled: true, filterExpression: null, dispatch: { mode: 'everyMatch' } });
export type TenantAutomationDraft = { presetId?: string | null; presetVersion?: number | null; targetSessionId?: string | null; reportToSessionId?: string | null; mode?: 'schedule' | 'events'; events?: TenantAutomationEvent[]; enabled: boolean; name: string; instructions: string; schedule: ScheduleTiming;
  offeringId: string | null; reasoningEffort: string | null; requiresTools: boolean; requiresVision: boolean };
export const tenantAutomationsApi = (tenant: string, instance: string, suffix = '') => tenantAgentApi(tenant, instance, '/automations' + suffix);
export const automationDraft = (value?: TenantAutomation): TenantAutomationDraft | undefined => {
  if (!value || !['backgroundSession', 'existingSession'].includes(value.executionTarget) || !['none', 'toSession'].includes(value.completionDelivery)
    || value.completionDelivery === 'toSession' && !value.reportToSessionId
    || value.executionTarget === 'existingSession' && (!value.targetSessionId || value.completionDelivery !== 'none' || value.reportToSessionId != null)
    || value.executionTarget === 'backgroundSession' && value.targetSessionId != null) return undefined;
  const base = { presetId: value.presetId ?? null, presetVersion: value.presetVersion ?? null, targetSessionId: value.executionTarget === 'existingSession' ? value.targetSessionId : null, reportToSessionId: value.completionDelivery === 'toSession' ? value.reportToSessionId : null, enabled: value.status === 'active', name: value.name, instructions: value.instructions, offeringId: value.modelOverrideOfferingId,
    reasoningEffort: value.modelOverrideReasoningEffort, requiresTools: value.requiresTools, requiresVision: value.requiresVision };
  if (value.triggers.length === 1 && value.triggers[0].kind === 'schedule' && value.triggers[0].schedule)
    return { ...base, schedule: { ...value.triggers[0].schedule } };
  if (!value.triggers.length || value.triggers.length > 32 || value.triggers.some(t => t.kind !== 'event' || !t.triggerId || !t.source || !Number.isSafeInteger(t.revision))) return undefined;
  return { ...base, mode: 'events', schedule: { kind: 'oneShot', timeZone: 'UTC', interval: 1 }, events: value.triggers.map(t => ({ localKey: t.triggerId!, triggerId: t.triggerId,
    revision: t.revision!, enabled: t.enabled !== false, source: { ...t.source! }, filterExpression: t.filterExpression ?? null, dispatch: { ...(t.dispatch ?? { mode: 'everyMatch' }) } })) };
};
export function automationPresetDraft(preset: TenantAutomationPreset | undefined, timing: ScheduleTiming): TenantAutomationDraft {
  const value: TenantAutomationDraft = { presetId: preset?.presetId ?? null, presetVersion: preset?.presetVersion ?? null, enabled: false,
    name: preset?.name ?? '', instructions: preset?.instructions ?? '', schedule: preset ? { ...timing, kind: 'weekly', interval: 1, weekdays: [1], maxOccurrences: 52 } : timing,
    offeringId: null, reasoningEffort: null, requiresTools: true, requiresVision: false, targetSessionId: null, reportToSessionId: null };
  return preset?.triggerKind === 'coreEvent' ? { ...value, mode: 'events', events: [{ ...newTenantAutomationEvent(),
    source: preset.coreEventKey ? { kind: 'builtin', key: preset.coreEventKey } : undefined, filterExpression: preset.filterExpression, dispatch: { ...(preset.dispatch ?? { mode: 'everyMatch' }) } }] } : value;
}
export const validTenantAutomationEvents = (events: TenantAutomationEvent[] = []) => events.length > 0 && events.length <= 32
  && new Set(events.map(e => e.source?.kind === 'builtin' ? `builtin:${e.source.key}` : e.source ? `webhook:${e.source.eventId}` : '')).size === events.length
  && events.every(e => !!e.source && (e.source.kind === 'builtin' ? !!e.source.key : !!e.source.eventId)
    && new TextEncoder().encode(e.filterExpression ?? '').length <= 1024
    && (e.dispatch.mode === 'everyMatch' ? e.dispatch.windowSeconds == null : Number.isInteger(e.dispatch.windowSeconds) && e.dispatch.windowSeconds! >= 60 && e.dispatch.windowSeconds! <= 3600));
export const automationWitness = (view: TenantAutomationEditor, value?: TenantAutomation) => ({ expectedRevision: view.securityRevision,
  expectedInstanceRevision: view.agent.instance.revision, expectedAutomationRevision: value?.revision ?? 0 });
export async function saveTenantAutomation(tenant: string, instance: string, view: TenantAutomationEditor, draft: TenantAutomationDraft, value: TenantAutomation | undefined, signal: AbortSignal) {
  const result = await identityRequest<TenantAutomation>(tenantAutomationsApi(tenant, instance, value ? `/${encodeURIComponent(value.automationId)}` : ''), {
    method: value ? 'PUT' : 'POST', signal, body: { enabled: draft.enabled, name: draft.name.trim(), instructions: draft.instructions.trim(), offeringId: draft.offeringId, reasoningEffort: draft.reasoningEffort,
      presetId: draft.presetId ?? null, presetVersion: draft.presetVersion ?? null,
      requiresTools: draft.requiresTools, requiresVision: draft.requiresVision, targetSessionId: draft.targetSessionId || null, reportToSessionId: draft.reportToSessionId || null,
      ...(draft.mode === 'events' ? { triggers: draft.events?.map(e => ({ kind: 'event', triggerId: e.triggerId, revision: e.revision, enabled: e.enabled,
        source: e.source, filterExpression: e.filterExpression, dispatch: e.dispatch })) } : { schedule: draft.schedule }), ...automationWitness(view, value) }
  }).catch(error => { if (error instanceof IdentityCompletionError) throw new IdentityCompletionError(false); throw error; });
  if (!result?.automationId || value && result.automationId !== value.automationId || !Number.isSafeInteger(result.revision) || result.revision < 1) throw new IdentityCompletionError(false);
  if ((result.targetSessionId ?? null) !== (draft.targetSessionId || null) || result.executionTarget !== (draft.targetSessionId ? 'existingSession' : 'backgroundSession')) throw new IdentityCompletionError(false);
  if ((result.reportToSessionId ?? null) !== (draft.reportToSessionId || null) || result.completionDelivery !== (draft.reportToSessionId ? 'toSession' : 'none')) throw new IdentityCompletionError(false);
  if ((result.presetId ?? null) !== (draft.presetId ?? null) || (result.presetVersion ?? null) !== (draft.presetVersion ?? null)) throw new IdentityCompletionError(false);
  return result;
}

export type TenantAutomationEventActivity = { eventId: string; triggerId: string; triggerRevision: number; sourceKind: 'builtin' | 'webhook'; sourceKey: string;
  receivedAtUtc: string; occurredAtUtc: string; status: string; code: string | null; decision: string | null; batchReadable: boolean;
  bucketId: string | null; batchCount: number | null; occurrenceId: string | null; runId: string | null; sessionId: string | null; runStatus: string | null; approvalRequestId: string | null; data: Record<string, unknown> | null };
export type TenantAutomationEventActivityPage = { items: TenantAutomationEventActivity[]; total: number; page: number; pageSize: number; securityRevision: number };

export function tenantAutomationConversations(tenant: string): SessionPickerSource {
  type Metadata = Omit<SessionPickerItem, 'archived' | 'ended'> & { archivedAt: string | null };
  return {
    async list(options) {
      const page = options.cursor ? Number(options.cursor) : 0;
      const value = await identityRequest<{ items: Metadata[]; total: number }>(tenantApi(tenant, `/sessions?page=${page}&pageSize=${options.limit}&includeArchived=true`));
      return { items: value.items.map(item => ({ ...item, archived: item.archivedAt !== null, ended: ['ended', 'ending'].includes(item.status) })),
        hasMore: (page + 1) * options.limit < value.total, nextCursor: (page + 1) * options.limit < value.total ? String(page + 1) : null };
    },
    read: id => identityRequest<Metadata>(tenantApi(tenant, `/sessions/${encodeURIComponent(id)}`)),
  };
}
