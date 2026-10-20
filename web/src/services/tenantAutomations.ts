import type { AutomationPolicy, ScheduleTiming } from './adminApi';
import type { TenantAgentEditor } from './tenantAgentConfiguration';
import { tenantAgentApi } from './tenantAgentConfiguration';
import { IdentityCompletionError, identityRequest } from './identity';

export type TenantAutomation = {
  automationId: string; name: string; instructions: string; status: string; revision: number; triggerRevision: number;
  nextOccurrenceAtUtc: string | null; occurrenceCount: number; suspensionReason: string | null;
  modelOverrideCatalogKey: string | null; modelOverrideReasoningEffort: string | null; requiresTools: boolean; requiresVision: boolean;
  executionTarget: string; completionDelivery: string; createdAt: string; updatedAt: string;
  triggers: { kind: string; schedule?: ScheduleTiming }[];
};
export type TenantAutomationItem = { automation: TenantAutomation; canUpdate: boolean; canCancel: boolean; canRun: boolean };
export type TenantAutomationEditor = Pick<TenantAgentEditor, 'agent' | 'securityRevision'> & {
  policy: AutomationPolicy | null; items: TenantAutomationItem[]; total: number; page: number; pageSize: number; canCreate: boolean; canSchedule: boolean;
};
export type TenantAutomationDraft = { enabled: boolean; name: string; instructions: string; schedule: ScheduleTiming;
  modelKey: string | null; reasoningEffort: string | null; requiresTools: boolean; requiresVision: boolean };
export const tenantAutomationsApi = (tenant: string, instance: string, suffix = '') => tenantAgentApi(tenant, instance, '/automations' + suffix);
export const automationDraft = (value?: TenantAutomation): TenantAutomationDraft | undefined => value && value.triggers.length === 1
  && value.triggers[0].kind === 'schedule' && value.triggers[0].schedule && value.executionTarget === 'backgroundSession' && value.completionDelivery === 'none'
  ? { enabled: value.status === 'active', name: value.name, instructions: value.instructions, schedule: { ...value.triggers[0].schedule }, modelKey: value.modelOverrideCatalogKey,
    reasoningEffort: value.modelOverrideReasoningEffort, requiresTools: value.requiresTools, requiresVision: value.requiresVision } : undefined;
export const automationWitness = (view: TenantAutomationEditor, value?: TenantAutomation) => ({ expectedRevision: view.securityRevision,
  expectedInstanceRevision: view.agent.instance.revision, expectedAutomationRevision: value?.revision ?? 0 });
export async function saveTenantAutomation(tenant: string, instance: string, view: TenantAutomationEditor, draft: TenantAutomationDraft, value: TenantAutomation | undefined, signal: AbortSignal) {
  const result = await identityRequest<TenantAutomation>(tenantAutomationsApi(tenant, instance, value ? `/${encodeURIComponent(value.automationId)}` : ''), {
    method: value ? 'PUT' : 'POST', signal, body: { ...draft, name: draft.name.trim(), instructions: draft.instructions.trim(), ...automationWitness(view, value) }
  });
  if (!result?.automationId || value && result.automationId !== value.automationId || !Number.isSafeInteger(result.revision) || result.revision < 1) throw new IdentityCompletionError();
  return result;
}
