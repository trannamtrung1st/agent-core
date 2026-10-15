import { ownerFetch } from './api';
import { adminProblemMessage } from './adminApi';
import type { SettingsSection, SettingValue } from './instanceConfiguration';

export type IsolationRevisions = { instance: number; experience: number; maintenance: number };
export type IsolationPatch = { section: string; set: Record<string, SettingValue>; clear: string[] };
export type IsolationDraft = { settings: IsolationPatch[]; experienceEnabled: boolean; allowMaintenance: boolean };
export type IsolationView = { revisions: IsolationRevisions; settings: SettingsSection[]; experienceEnabled: boolean; allowMaintenance: boolean; hostBrowserMode: string; browserEnabled: boolean };
export type IsolationPlan = { expected: IsolationRevisions; draft: IsolationDraft; fingerprint: string;
  changes: { owner: string; field: string; before: SettingValue; after: SettingValue; source: string }[]; blockingReasons: string[]; notice: string };
export type IsolationPreset = 'Persistent' | 'SessionIsolated' | 'Stateless';
async function request<T>(id: string, suffix: string, signal: AbortSignal | undefined, body?: unknown): Promise<T> {
  const response = await ownerFetch(`/api/v2/admin/agent-instances/${encodeURIComponent(id)}/isolation${suffix}`, {
    signal, ...(body === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }),
  });
  if (!response.ok) throw await adminProblemMessage(response, 'State & Isolation could not be saved. Refresh the preview; your draft is retained.');
  return response.json() as Promise<T>;
}
export const readIsolation = (id: string, signal?: AbortSignal) => request<IsolationView>(id, '', signal);
export const presetIsolation = (id: string, preset: IsolationPreset, signal: AbortSignal) => request<IsolationPlan>(id, '/preset', signal, { preset });
export const previewIsolation = (id: string, draft: IsolationDraft, signal: AbortSignal) => request<IsolationPlan>(id, '/preview', signal, draft);
export const applyIsolation = (id: string, plan: IsolationPlan) => request<IsolationView>(id, '/apply', undefined, { expected: plan.expected, draft: plan.draft, fingerprint: plan.fingerprint });

export function isolationValue(view: IsolationView, draft: IsolationDraft, section: string, field: string): SettingValue {
  const saved = view.settings.find(s => s.section === section)!;
  const patch = draft.settings.find(s => s.section === section);
  return patch?.clear.includes(field) ? saved.definitionDefaults[field] : patch && field in patch.set ? patch.set[field] : saved.effective[field];
}

export function deriveIsolationPreset(view: IsolationView, draft: IsolationDraft): IsolationPreset | 'Custom' {
  const state = (field: string) => isolationValue(view, draft, 'stateIsolationPolicy', field);
  const memory = view.settings.find(s => s.section === 'memoryPolicy')!;
  const isolated = state('homeAccess') === 'Disabled' && state('browserIsolation') === 'EphemeralSession'
    && ['experienceRecord', 'experienceRecall', 'profileEvidence', 'crossSessionContinuity'].every(f => state(f) === false)
    && state('agentStateVisibility') !== 'InstanceSummary' && !draft.experienceEnabled && !draft.allowMaintenance
    && Object.keys(memory.effective).filter(f => f !== 'sessionMemory').every(f => isolationValue(view, draft, 'memoryPolicy', f) === false);
  if (isolated && state('conversationContextScope') === 'CurrentRequest'
    && Object.keys(memory.effective).every(f => isolationValue(view, draft, 'memoryPolicy', f) === false)
    && isolationValue(view, draft, 'voice', 'enabled') === false) return 'Stateless';
  if (isolated && state('conversationContextScope') === 'Session') return 'SessionIsolated';
  if (['stateIsolationPolicy', 'memoryPolicy'].every(section => {
    const s = view.settings.find(s => s.section === section)!;
    return Object.keys(s.effective).every(field => JSON.stringify(isolationValue(view, draft, section, field)) === JSON.stringify(s.definitionDefaults[field]));
  })) return 'Persistent';
  return 'Custom';
}
