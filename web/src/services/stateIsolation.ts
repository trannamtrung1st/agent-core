import type { SettingsSection,SettingValue } from './instanceConfiguration';
import { isolationPresets,isolationRecipe,type IsolationPreset } from './isolationPresets';
export type { IsolationPreset,IsolationPresetAction } from './isolationPresets';

export type IsolationRevisions = { instance: number; experience: number; maintenance: number };
export type IsolationPatch = { section: string; set: Record<string, SettingValue>; clear: string[] };
export type IsolationDraft = { settings: IsolationPatch[]; experienceEnabled: boolean; allowMaintenance: boolean };
export type IsolationView = { revisions: IsolationRevisions; settings: SettingsSection[]; experienceEnabled: boolean; allowMaintenance: boolean; hostBrowserMode: string; browserEnabled: boolean; sandboxEnforcementState?: 'Enforced' | 'EnforcementPending' | 'NotObserved' };
export type IsolationPlan = { expected: IsolationRevisions; draft: IsolationDraft; fingerprint: string;
  changes: { owner: string; field: string; before: SettingValue; after: SettingValue; source: string }[]; blockingReasons: string[]; notice: string };

export function isolationValue(view: IsolationView, draft: IsolationDraft, section: string, field: string): SettingValue {
  const saved = view.settings.find(s => s.section === section)!;
  const patch = draft.settings.find(s => s.section === section);
  return patch?.clear.includes(field) ? saved.definitionDefaults[field] : patch && field in patch.set ? patch.set[field] : saved.effective[field];
}

export function deriveIsolationPreset(view: IsolationView, draft: IsolationDraft): IsolationPreset | 'Custom' {
  const state = (field: string) => isolationValue(view, draft, 'stateIsolationPolicy', field);
  const memory = view.settings.find(s => s.section === 'memoryPolicy')!;
  const policy = view.settings.find(s => s.section === 'stateIsolationPolicy')!;
  const visibility = policy.constraints?.agentStateVisibility?.choices?.includes('CurrentSession') ? 'CurrentSession' : 'Off';
  return isolationPresets.find(preset => {
    const recipe = isolationRecipe(preset, { memoryDefaults: memory.definitionDefaults, sessionMemory: memory.effective.sessionMemory === true,
      voiceEnabled: view.settings.find(s => s.section === 'voice')!.effective.enabled === true, visibility,
      experienceEnabled: view.experienceEnabled, allowMaintenance: view.allowMaintenance });
    return Object.entries(recipe.state).every(([field, value]) => state(field) === value)
      && Object.entries(recipe.memory).every(([field, value]) => isolationValue(view, draft, 'memoryPolicy', field) === value)
      && isolationValue(view, draft, 'voice', 'enabled') === recipe.voiceEnabled
      && draft.experienceEnabled === recipe.experienceEnabled && draft.allowMaintenance === recipe.allowMaintenance;
  }) ?? 'Custom';
}
