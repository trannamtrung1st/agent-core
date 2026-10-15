import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { ConfigProvider } from 'antd';
import { beforeEach, expect, test, vi } from 'vitest';
import { StateIsolationConfigurationSection } from './StateIsolationConfigurationSection';
import { applyIsolation, deriveIsolationPreset, presetIsolation, previewIsolation, readIsolation, type IsolationDraft, type IsolationPlan, type IsolationView } from '../../services/stateIsolation';

vi.mock('../../services/stateIsolation', async importOriginal => ({ ...await importOriginal<typeof import('../../services/stateIsolation')>(),
  applyIsolation: vi.fn(), presetIsolation: vi.fn(), previewIsolation: vi.fn(), readIsolation: vi.fn() }));
const state = { conversationContextScope: 'Session', homeAccess: 'ReadWrite', workingCleanup: 'OnSessionDeletion', browserIsolation: 'HostDefault', agentStateVisibility: 'Off',
  experienceRecord: true, experienceRecall: true, profileEvidence: true, crossSessionContinuity: true };
const section = (name: string, effective: Record<string, string | boolean>) => ({ section: name, instanceRevision: 1, definitionId: 'definition', definitionVersion: 1,
  overrides: {}, effective, definitionDefaults: effective, configurationHash: 'hash', sources: Object.fromEntries(Object.keys(effective).map(k => [k, 'definition' as const])),
  constraints: { homeAccess: { reason: 'Narrow only', requiredBoolean: null, minimum: null, maximum: null, choices: ['ReadWrite', 'ReadOnly', 'Disabled'] } } });
const view: IsolationView = { revisions: { instance: 1, experience: 0, maintenance: 0 }, experienceEnabled: false, allowMaintenance: false,
  hostBrowserMode: 'PersistentAgent', browserEnabled: true, settings: [section('stateIsolationPolicy', state), section('memoryPolicy', { sessionMemory: true, identityUserPromotion: false, identityUserRetrieval: false, userPromotion: false, userRetrieval: false }), section('voice', { enabled: false })] };
const isolated: IsolationDraft = { settings: [{ section: 'stateIsolationPolicy', set: { ...state, homeAccess: 'Disabled', browserIsolation: 'EphemeralSession', experienceRecord: false, experienceRecall: false, profileEvidence: false, crossSessionContinuity: false }, clear: [] }], experienceEnabled: false, allowMaintenance: false };
const plan = (draft: IsolationDraft): IsolationPlan => ({ expected: view.revisions, draft, fingerprint: 'fingerprint', blockingReasons: [], notice: 'Existing data is retained.',
  changes: draft.settings.length ? [{ owner: 'stateIsolationPolicy', field: 'homeAccess', before: 'ReadWrite', after: draft.settings[0].set.homeAccess, source: 'instance' }] : [] });
beforeEach(() => {
  vi.mocked(readIsolation).mockReset().mockResolvedValue(view);
  vi.mocked(previewIsolation).mockReset().mockImplementation(async (_id, draft) => plan(draft));
  vi.mocked(presetIsolation).mockReset().mockResolvedValue(plan(isolated));
  vi.mocked(applyIsolation).mockReset();
});

test('preset label derives from underlying fields and an independent edit becomes Custom', () => {
  expect(deriveIsolationPreset(view, { settings: [], experienceEnabled: false, allowMaintenance: false })).toBe('Persistent');
  expect(deriveIsolationPreset(view, isolated)).toBe('SessionIsolated');
  expect(deriveIsolationPreset(view, { ...isolated, settings: [{ ...isolated.settings[0], set: { ...isolated.settings[0].set, homeAccess: 'ReadOnly' } }] })).toBe('Custom');
  expect(deriveIsolationPreset(view, { ...isolated, settings: [...isolated.settings, { section: 'memoryPolicy', set: { sessionMemory: false }, clear: [] }].map(p => p.section === 'stateIsolationPolicy' ? { ...p, set: { ...p.set, conversationContextScope: 'CurrentRequest' } } : p) })).toBe('Stateless');
});

test('selecting a preset is a draft and a conflict preserves it for refresh', async () => {
  vi.mocked(applyIsolation).mockRejectedValue(new Error('Owner revision changed. Refresh the preview.'));
  render(<ConfigProvider theme={{ token: { motion: false } }}><StateIsolationConfigurationSection instanceId="owner" active archived={false} onUpdated={() => {}} onDirtyChange={() => {}} /></ConfigProvider>);
  const preset = await screen.findByRole('combobox', { name: 'Isolation preset' });
  await waitFor(() => expect(preset).not.toBeDisabled());
  fireEvent.mouseDown(preset);
  fireEvent.click(await screen.findByText('Session-isolated assistant'));
  await waitFor(() => expect(presetIsolation).toHaveBeenCalled());
  expect(applyIsolation).not.toHaveBeenCalled();
  await waitFor(() => expect(screen.getByRole('button', { name: 'Apply changes' })).not.toBeDisabled());
  fireEvent.click(screen.getByRole('button', { name: 'Apply changes' }));
  await screen.findByText('Owner revision changed. Refresh the preview.');
  expect(preset.parentElement).toHaveTextContent('Session-isolated assistant');
  expect(applyIsolation).toHaveBeenCalledTimes(1);
});
