export type ReasoningPreference = { mode: 'modelDefault' | 'fixed' | 'auto'; effort?: string | null };
export type ModelPreference = { mode: 'inherit' | 'fixed' | 'auto'; offeringId?: string | null; reasoning?: ReasoningPreference | null };
export function preferenceFromFields(key: string | null | undefined, effort: string | null | undefined): ModelPreference {
  return { mode: key === 'auto' ? 'auto' : !key || key === 'default' ? 'inherit' : 'fixed',
    offeringId: key && key !== 'default' && key !== 'auto' ? key : null,
    reasoning: (!key || key === 'default') && !effort ? null : { mode: effort === 'auto' ? 'auto' : effort && effort !== 'modelDefault' ? 'fixed' : 'modelDefault', effort: effort && effort !== 'auto' && effort !== 'modelDefault' ? effort : null } };
}
export function preferenceFields(value: ModelPreference | null | undefined) {
  return { key: value?.mode === 'auto' ? 'auto' : value?.mode === 'fixed' ? value.offeringId ?? '' : '',
    effort: value?.reasoning?.mode === 'auto' ? 'auto' : value?.reasoning?.mode === 'fixed' ? value.reasoning.effort ?? '' : value?.mode === 'inherit' && value.reasoning?.mode === 'modelDefault' ? 'modelDefault' : '' };
}
export function preferenceLabel(value: ModelPreference | null | undefined) {
  return value?.mode === 'auto' ? 'Auto' : value?.mode === 'fixed' ? value.offeringId ?? 'Unavailable fixed model' : 'Inherited default';
}

export type ModelDecisionEvidence = { basis: string; engineId: string; engineVersion: string; settingsRevision: number; preferenceSource: string; policyRevision: string; diagnosticCode: string | null };

export function modelPreferenceKey(preference: ModelPreference | null | undefined, fallback: string | null): string | null {
  return preference?.mode === 'auto' ? 'auto' : preference?.mode === 'inherit' ? 'default'
    : preference?.mode === 'fixed' ? preference.offeringId ?? fallback : fallback;
}
