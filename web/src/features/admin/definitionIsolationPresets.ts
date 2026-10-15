import { isolationRecipe, isolationPresets, memoryFields, stateIsolationDefaults, type IsolationPreset } from '../../services/isolationPresets';
import type { IsolationPlan } from '../../services/stateIsolation';
import type { SettingValue } from '../../services/instanceConfiguration';
import { applyVoiceEnabled, type DefinitionCandidate } from './definitionCandidate';

export const isolationRecord = (value: unknown): Record<string, unknown> => value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {};
const memoryOf = (candidate: DefinitionCandidate) => Object.fromEntries(memoryFields.map(field => [field, isolationRecord(candidate.memoryPolicy)[field] === true]));
export function definitionStatePolicy(candidate: DefinitionCandidate): Record<string, unknown> {
  const policy = isolationRecord(candidate.stateIsolationPolicy);
  return { ...stateIsolationDefaults, ...policy,
    maxAgentStateVisibility: policy.maxAgentStateVisibility ?? policy.agentStateVisibility ?? stateIsolationDefaults.agentStateVisibility };
}
function recipe(preset: IsolationPreset, saved: DefinitionCandidate) {
  const memory = memoryOf(saved);
  const result = isolationRecipe(preset, { memoryDefaults: memory, sessionMemory: memory.sessionMemory, voiceEnabled: isolationRecord(saved.voice).enabled === true,
    visibility: 'CurrentSession', experienceEnabled: false, allowMaintenance: false });
  return { ...result, state: { ...result.state, maxAgentStateVisibility: result.state.agentStateVisibility } };
}
export function definitionIsolationPreset(candidate: DefinitionCandidate, saved: DefinitionCandidate): IsolationPreset | 'Custom' {
  const state = definitionStatePolicy(candidate);
  const memory = memoryOf(candidate);
  return isolationPresets.find(preset => {
    const next = recipe(preset, saved);
    return Object.entries(next.state).every(([field, value]) => state[field] === value)
      && Object.entries(next.memory).every(([field, value]) => memory[field] === value)
      && (isolationRecord(candidate.voice).enabled === true) === next.voiceEnabled;
  }) ?? 'Custom';
}
export function applyDefinitionIsolationPreset(candidate: DefinitionCandidate, saved: DefinitionCandidate, preset: IsolationPreset): DefinitionCandidate {
  const next = recipe(preset, saved);
  const providers = isolationRecord(saved.providerPreferences);
  return applyVoiceEnabled({ ...candidate, stateIsolationPolicy: next.state, memoryPolicy: { ...isolationRecord(candidate.memoryPolicy), ...next.memory } }, next.voiceEnabled, {
    speechRecognizer: typeof providers.speechRecognizer === 'string' ? providers.speechRecognizer : null,
    speechSynthesizer: typeof providers.speechSynthesizer === 'string' ? providers.speechSynthesizer : null
  });
}
export function definitionIsolationChanges(candidate: DefinitionCandidate, saved: DefinitionCandidate): IsolationPlan['changes'] {
  const sections = { stateIsolationPolicy: [...Object.keys(stateIsolationDefaults), 'maxAgentStateVisibility'], memoryPolicy: [...memoryFields], voice: ['enabled'], providerPreferences: ['speechRecognizer', 'speechSynthesizer'] };
  const value = (source: DefinitionCandidate, section: string, field: string): SettingValue => {
    if (section === 'stateIsolationPolicy') return definitionStatePolicy(source)[field] as SettingValue;
    return (isolationRecord(source[section])[field] ?? (section === 'memoryPolicy' || section === 'voice' ? false : null)) as SettingValue;
  };
  return Object.entries(sections).flatMap(([owner, fields]) => fields.flatMap(field => {
    const before = value(saved, owner, field), after = value(candidate, owner, field);
    return before === after ? [] : [{ owner, field, before, after, source: 'draft' }];
  }));
}
