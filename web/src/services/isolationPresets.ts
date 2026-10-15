import type { SettingValue } from './instanceConfiguration';

export type IsolationPreset = 'Persistent' | 'SessionIsolated' | 'Stateless';
export type IsolationPresetAction = IsolationPreset | 'DefinitionDefaults';
export const stateIsolationDefaults: Record<string, string | boolean> = {
  conversationContextScope: 'Session', homeAccess: 'ReadWrite', workingCleanup: 'OnSessionDeletion',
  browserIsolation: 'HostDefault', agentStateVisibility: 'Off', experienceRecord: true,
  experienceRecall: true, profileEvidence: true, crossSessionContinuity: true
};
export const memoryFields = ['sessionMemory', 'identityUserPromotion', 'identityUserRetrieval', 'userPromotion', 'userRetrieval'] as const;
export type IsolationRecipeContext = {
  memoryDefaults: Record<string, SettingValue>; sessionMemory: boolean; voiceEnabled: boolean;
  visibility: string; experienceEnabled: boolean; allowMaintenance: boolean;
};
export function isolationRecipe(preset: IsolationPreset, context: IsolationRecipeContext) {
  const persistent = preset === 'Persistent';
  return {
    state: persistent ? { ...stateIsolationDefaults } : {
      conversationContextScope: preset === 'Stateless' ? 'CurrentRequest' : 'Session', homeAccess: 'Disabled',
      workingCleanup: 'OnSessionDeletion', browserIsolation: 'EphemeralSession', agentStateVisibility: context.visibility,
      experienceRecord: false, experienceRecall: false, profileEvidence: false, crossSessionContinuity: false
    },
    memory: Object.fromEntries(memoryFields.map(field => [field, persistent ? context.memoryDefaults[field] === true
      : field === 'sessionMemory' && preset !== 'Stateless' && context.sessionMemory])),
    voiceEnabled: preset !== 'Stateless' && context.voiceEnabled,
    experienceEnabled: persistent && context.experienceEnabled,
    allowMaintenance: persistent && context.allowMaintenance
  };
}
export const isolationPresets: IsolationPreset[] = ['Persistent', 'SessionIsolated', 'Stateless'];
