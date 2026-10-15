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

const stateless: IsolationDraft = { ...isolated, settings: [
  { ...isolated.settings[0], set: { ...isolated.settings[0].set, conversationContextScope: 'CurrentRequest' } },
  { section: 'memoryPolicy', set: { sessionMemory: false }, clear: [] },
  { section: 'voice', set: { enabled: false }, clear: [] },
] };
const mutate = (draft: IsolationDraft, section: string, field: string, value: string | boolean): IsolationDraft => {
  const existing = draft.settings.find(p => p.section === section);
  return { ...draft, settings: [...draft.settings.filter(p => p.section !== section),
    { section, set: { ...existing?.set, [field]: value }, clear: [] }] };
};
const alteredState: Record<string, string | boolean> = { conversationContextScope: 'CurrentRequest', homeAccess: 'ReadOnly',
  workingCleanup: 'AfterTerminal', browserIsolation: 'HostDefault', agentStateVisibility: 'CurrentSession',
  experienceRecord: true, experienceRecall: true, profileEvidence: true, crossSessionContinuity: true };
for (const [name, recipe] of [['SessionIsolated', isolated], ['Stateless', stateless]] as const) {
  test.each(Object.entries(alteredState))(`${name}: independently editing %s derives Custom`, (field, alternate) => {
    const value = field === 'conversationContextScope' && name === 'Stateless' ? 'Session' : alternate;
    const edited = mutate(recipe, 'stateIsolationPolicy', field, value);
    // Changing request scope alone matches Session-isolated only if its Memory recipe also matches.
    expect(deriveIsolationPreset(view, edited)).toBe('Custom');
  });
  test.each(Object.keys(view.settings[1].effective))(`${name}: editing Memory %s derives Custom`, field => {
    const current = field === 'sessionMemory' && name === 'SessionIsolated';
    expect(deriveIsolationPreset(view, mutate(recipe, 'memoryPolicy', field, !current))).toBe('Custom');
  });
  test(`${name}: Experience, maintenance and Voice edits derive Custom`, () => {
    expect(deriveIsolationPreset(view, { ...recipe, experienceEnabled: true })).toBe('Custom');
    expect(deriveIsolationPreset(view, { ...recipe, allowMaintenance: true })).toBe('Custom');
    expect(deriveIsolationPreset(view, mutate(recipe, 'voice', 'enabled', true))).toBe('Custom');
  });
}

test('Persistent matches inherited recipes and preserves saved enablement flags', () => {
  const initial = { settings: [], experienceEnabled: false, allowMaintenance: false };
  for (const section of view.settings.slice(0, 2)) {
    for (const [field, value] of Object.entries(section.effective)) {
      expect(deriveIsolationPreset(view, mutate(initial, section.section, field,
        typeof value === 'boolean' ? !value : field === 'conversationContextScope' ? 'CurrentRequest' : 'Changed'))).toBe('Custom');
    }
  }
  expect(deriveIsolationPreset(view, { ...initial, experienceEnabled: true })).toBe('Custom');
  expect(deriveIsolationPreset(view, { ...initial, allowMaintenance: true })).toBe('Custom');
  expect(deriveIsolationPreset(view, mutate(initial, 'voice', 'enabled', true))).toBe('Custom');
});

test('visibility recipe follows Definition constraints and inheritance resolves the effective cleanup', () => {
  const scoped: IsolationView = { ...view, settings: view.settings.map(s => s.section === 'stateIsolationPolicy'
    ? { ...s, constraints: { ...s.constraints, agentStateVisibility: { ...s.constraints!.homeAccess, choices: ['Off', 'CurrentSession'] } } } : s) };
  expect(deriveIsolationPreset(scoped, isolated)).toBe('Custom');
  expect(deriveIsolationPreset(scoped, mutate(isolated, 'stateIsolationPolicy', 'agentStateVisibility', 'CurrentSession'))).toBe('SessionIsolated');
  const cleanup = mutate(isolated, 'stateIsolationPolicy', 'workingCleanup', 'AfterTerminal');
  expect(deriveIsolationPreset(view, { ...cleanup, settings: cleanup.settings.map(s => ({ ...s,
    set: Object.fromEntries(Object.entries(s.set).filter(([key]) => key !== 'workingCleanup')), clear: ['workingCleanup'] })) })).toBe('SessionIsolated');
});

test('inheriting isolated Definition defaults is never labeled Persistent', () => {
  const defaults: IsolationView = { ...view, settings: view.settings.map(s => s.section === 'stateIsolationPolicy'
    ? { ...s, effective: { ...isolated.settings[0].set, conversationContextScope: 'CurrentRequest' }, definitionDefaults: { ...isolated.settings[0].set, conversationContextScope: 'CurrentRequest' } }
    : s.section === 'memoryPolicy' ? { ...s, effective: Object.fromEntries(Object.keys(s.effective).map(field => [field, false])), definitionDefaults: Object.fromEntries(Object.keys(s.effective).map(field => [field, false])) } : s) };
  expect(deriveIsolationPreset(defaults, { settings: [], experienceEnabled: false, allowMaintenance: false })).toBe('Stateless');
});

test('Use Definition defaults requests a separate unsaved reset action', async () => {
  render(<ConfigProvider theme={{ token: { motion: false } }}><StateIsolationConfigurationSection instanceId="owner" active archived={false} onUpdated={() => {}} onDirtyChange={() => {}} /></ConfigProvider>);
  const reset = await screen.findByRole('button', { name: 'Use Definition defaults' });
  await waitFor(() => expect(reset).toBeEnabled());
  fireEvent.click(reset);
  await waitFor(() => expect(presetIsolation).toHaveBeenCalledWith('owner', 'DefinitionDefaults', expect.any(AbortSignal)));
  expect(applyIsolation).not.toHaveBeenCalled();
});

test.each(['clean', 'policy edit', 'preset'] as const)('refresh rebases untouched opt-ins and preserves explicit intent: %s', async kind => {
  const onDirtyChange = vi.fn();
  const props = { instanceId: 'owner', archived: false, onUpdated: () => {}, onDirtyChange };
  const surface = (active: boolean) => <ConfigProvider theme={{ token: { motion: false } }}><StateIsolationConfigurationSection {...props} active={active} /></ConfigProvider>;
  const { rerender } = render(surface(true));
  const experience = await screen.findByRole('switch', { name: 'Experience enablement' });
  await waitFor(() => expect(experience).not.toBeDisabled());
  if (kind === 'policy edit') fireEvent.click(screen.getByRole('switch', { name: 'Allow Experience recording' }));
  if (kind === 'preset') {
    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Isolation preset' }));
    fireEvent.click(await screen.findByText('Session-isolated assistant'));
    await waitFor(() => expect(presetIsolation).toHaveBeenCalledOnce());
    await waitFor(() => expect(screen.getByRole('switch', { name: 'Allow Experience recording' })).not.toBeChecked());
  }
  vi.mocked(readIsolation).mockResolvedValue({ ...view, revisions: { ...view.revisions, experience: 1, maintenance: 1 }, experienceEnabled: true, allowMaintenance: true });
  rerender(surface(false)); rerender(surface(true));
  await waitFor(() => expect(readIsolation).toHaveBeenCalledTimes(2));
  await waitFor(() => expect(experience).not.toBeDisabled());
  await waitFor(() => {
    if (kind === 'preset') {
      expect(experience).not.toBeChecked();
      expect(screen.getByRole('switch', { name: 'Autonomous identity maintenance' })).not.toBeChecked();
    } else {
      expect(experience).toBeChecked();
      expect(screen.getByRole('switch', { name: 'Autonomous identity maintenance' })).toBeChecked();
    }
  });
  expect(onDirtyChange).toHaveBeenLastCalledWith(kind !== 'clean');
  if (kind === 'policy edit') expect(screen.getByRole('switch', { name: 'Allow Experience recording' })).not.toBeChecked();
});

test('switching presets protects the dirty draft on cancel and replaces it only after confirmation', async () => {
  render(<ConfigProvider theme={{ token: { motion: false } }}><StateIsolationConfigurationSection instanceId="owner" active archived={false} onUpdated={() => {}} onDirtyChange={() => {}} /></ConfigProvider>);
  const preset = await screen.findByRole('combobox', { name: 'Isolation preset' });
  await waitFor(() => expect(preset).not.toBeDisabled());
  const experience = await screen.findByRole('switch', { name: 'Experience enablement' });
  await waitFor(() => expect(experience).not.toBeDisabled());
  fireEvent.click(experience);
  expect(experience).toBeChecked();
  fireEvent.mouseDown(preset);
  fireEvent.click(await screen.findByText('Session-isolated assistant'));
  // AntD test-mode ids collide with Select ids; browser verification checks the accessible name.
  const dialog = (await screen.findByText('Replace unsaved isolation changes?', { selector: '.ant-modal-title' })).closest('[role="dialog"]')!;
  expect(dialog).toHaveTextContent('Memory, Voice, Experience and maintenance');
  expect(presetIsolation).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
  expect(screen.getByRole('switch', { name: 'Experience enablement' })).toBeChecked();
  fireEvent.mouseDown(preset);
  fireEvent.click(await screen.findByText('Session-isolated assistant'));
  fireEvent.click(await screen.findByRole('button', { name: 'Replace draft' }));
  await waitFor(() => expect(presetIsolation).toHaveBeenCalledTimes(1));
  await waitFor(() => expect(screen.getByRole('switch', { name: 'Experience enablement' })).not.toBeChecked());
  expect(applyIsolation).not.toHaveBeenCalled();
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
  await waitFor(() => expect(screen.getByRole('button', { name: 'Apply changes' })).not.toBeDisabled(), { timeout: 5_000 });
  fireEvent.click(screen.getByRole('button', { name: 'Apply changes' }));
  await screen.findByText('Owner revision changed. Refresh the preview.');
  expect(preset.parentElement).toHaveTextContent('Session-isolated assistant');
  expect(applyIsolation).toHaveBeenCalledTimes(1);
});

test('inherit previews the Definition value and removes the redundant reset until edited again', async () => {
  vi.mocked(readIsolation).mockResolvedValue({ ...view, settings: view.settings.map(s => s.section === 'stateIsolationPolicy'
    ? { ...s, effective: { ...s.effective, experienceRecord: false }, overrides: { experienceRecord: false }, sources: { ...s.sources, experienceRecord: 'instance' } }
    : s) });
  render(<ConfigProvider theme={{ token: { motion: false } }}><StateIsolationConfigurationSection instanceId="owner" active archived={false} onUpdated={() => {}} onDirtyChange={() => {}} /></ConfigProvider>);
  const inherit = await screen.findByRole('button', { name: 'Inherit Allow Experience recording' });
  await waitFor(() => expect(inherit).not.toBeDisabled());
  fireEvent.click(inherit);
  expect(screen.getByRole('switch', { name: 'Allow Experience recording' })).toBeChecked();
  expect(screen.getByText('Will inherit on save')).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Inherit Allow Experience recording' })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('switch', { name: 'Allow Experience recording' }));
  expect(screen.getByRole('button', { name: 'Inherit Allow Experience recording' })).toBeEnabled();
  expect(applyIsolation).not.toHaveBeenCalled();
});
