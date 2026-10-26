import { describe, expect, it } from 'vitest';
import { modelPreferenceKey, preferenceFields, preferenceFromFields } from './modelPreference';

describe('canonical model preference', () => {
  it('keeps Auto distinct from inherit and concrete provider identity', () => {
    expect(preferenceFromFields('auto', 'auto')).toEqual({ mode: 'auto', offeringId: null, reasoning: { mode: 'auto', effort: null } });
    expect(preferenceFromFields('default', null)).toEqual({ mode: 'inherit', offeringId: null, reasoning: null });
    expect(preferenceFields({ mode: 'auto', reasoning: { mode: 'modelDefault' } })).toEqual({ key: 'auto', effort: '' });
  });
  it('roundtrips explicit fixed reasoning without treating a blank as Auto', () => {
    const intent = preferenceFromFields('offering-fixture', 'high');
    expect(intent.mode).toBe('fixed'); expect(intent.offeringId).toBe('offering-fixture');
    expect(preferenceFields(intent)).toEqual({ key: 'offering-fixture', effort: 'high' });
    expect(preferenceFromFields('', '').mode).toBe('inherit');
    expect(preferenceFromFields('', 'modelDefault')).toEqual({ mode: 'inherit', offeringId: null, reasoning: { mode: 'modelDefault', effort: null } });
    expect(preferenceFields(preferenceFromFields('', 'modelDefault')).effort).toBe('modelDefault');
    expect(modelPreferenceKey(preferenceFromFields('new-fixed', 'auto'), 'previous-observed-model')).toBe('new-fixed');
    expect(preferenceFromFields('auto', 'high').reasoning).toEqual({ mode: 'fixed', effort: 'high' });
  });
});
