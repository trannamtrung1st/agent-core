import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { useCallback, useState } from 'react';
import { App, ConfigProvider } from 'antd';
import { beforeEach, expect, test, vi } from 'vitest';
import { InstanceConfigurationActions, InstanceConfigurationProvider } from './InstanceConfigurationActions';
import { StateIsolationConfigurationProvider } from './StateIsolationConfigurationProvider';
import { StateIsolationConfigurationSection } from './StateIsolationConfigurationSection';
import { InstanceSettingsSection } from './InstanceSettingsSection';
import { RetainedAdminPage } from './RetainedAdminPage';
import type { InstancePage } from './adminNavigation';
import { applyIsolation, presetIsolation, previewIsolation, readIsolation, isolationValue, type IsolationDraft, type IsolationPlan, type IsolationView } from '../../services/stateIsolation';
import { isolationRecipe } from '../../services/isolationPresets';
import { listInstanceSettings, patchInstanceSettings, type SettingValue, type SettingsSection } from '../../services/instanceConfiguration';

vi.mock('../../services/stateIsolation', async original => ({ ...await original<typeof import('../../services/stateIsolation')>(),
  applyIsolation: vi.fn(), presetIsolation: vi.fn(), previewIsolation: vi.fn(), readIsolation: vi.fn() }));
vi.mock('../../services/instanceConfiguration', () => ({ listInstanceSettings: vi.fn(), patchInstanceSettings: vi.fn() }));
vi.mock('../../services/api', () => ({ listModels: vi.fn(async () => ({ defaultKey: null, models: [] })) }));
let current: IsolationView;
const copy = <T,>(value: T): T => structuredClone(value);
const section = (name: string, values: Record<string, SettingValue>): SettingsSection => ({ section: name, instanceRevision: 1,
  definitionId: 'agent', definitionVersion: 1, effective: values, definitionDefaults: { ...values }, overrides: {},
  sources: Object.fromEntries(Object.keys(values).map(field => [field, 'definition'])), configurationHash: 'hash',
  constraints: { agentStateVisibility: { reason: 'Maximum', requiredBoolean: null, minimum: null, maximum: null, choices: ['Off', 'CurrentSession', 'InstanceSummary'] } } });
function plan(draft: IsolationDraft): IsolationPlan {
  const blocked = isolationValue(current, draft, 'stateIsolationPolicy', 'conversationContextScope') === 'CurrentRequest'
    && isolationValue(current, draft, 'voice', 'enabled') === true;
  return { expected: copy(current.revisions), draft, fingerprint: JSON.stringify(current.revisions),
    blockingReasons: blocked ? ['Request isolation requires Voice off.'] : [], notice: 'Atomic reviewed draft.',
    changes: draft.settings.flatMap(patch => Object.keys(patch.set).concat(patch.clear).map(field => ({ owner: patch.section, field,
      before: current.settings.find(s => s.section === patch.section)!.effective[field],
      after: isolationValue(current, draft, patch.section, field), source: patch.clear.includes(field) ? 'definition' : 'instance' }))) };
}
function commit(draft: IsolationDraft) {
  current = copy(current); current.revisions.instance++;
  for (const patch of draft.settings) {
    const target = current.settings.find(s => s.section === patch.section)!;
    for (const [field, value] of Object.entries(patch.set)) { target.effective[field] = value; target.overrides[field] = value; target.sources[field] = 'instance'; }
    for (const field of patch.clear) { target.effective[field] = target.definitionDefaults[field]; delete target.overrides[field]; target.sources[field] = 'definition'; }
  }
  current.settings.forEach(s => { s.instanceRevision = current.revisions.instance; });
}
beforeEach(() => {
  vi.clearAllMocks();
  const recipe = isolationRecipe('Persistent', { memoryDefaults: { sessionMemory: true }, sessionMemory: true,
    voiceEnabled: true, visibility: 'CurrentSession', experienceEnabled: false, allowMaintenance: false });
  current = { revisions: { instance: 1, experience: 0, maintenance: 0 }, experienceEnabled: false, allowMaintenance: false,
    browserEnabled: true, hostBrowserMode: 'PersistentAgent', settings: [section('stateIsolationPolicy', recipe.state as Record<string, SettingValue>),
      section('memoryPolicy', recipe.memory), section('voice', { enabled: true, voiceId: 'original', speakingRate: 1 })] };
  vi.mocked(readIsolation).mockImplementation(async () => copy(current));
  vi.mocked(listInstanceSettings).mockImplementation(async () => copy(current.settings));
  vi.mocked(previewIsolation).mockImplementation(async (_id, draft) => plan(draft));
  vi.mocked(presetIsolation).mockImplementation(async (_id, name) => {
    const recipe = isolationRecipe(name === 'DefinitionDefaults' ? 'Persistent' : name, { memoryDefaults: { sessionMemory: true },
      sessionMemory: true, voiceEnabled: true, visibility: 'CurrentSession', experienceEnabled: false, allowMaintenance: false });
    return plan({ settings: [{ section: 'stateIsolationPolicy', set: recipe.state as Record<string, SettingValue>, clear: [] },
      { section: 'memoryPolicy', set: recipe.memory, clear: [] }, { section: 'voice', set: { enabled: recipe.voiceEnabled }, clear: [] }],
      experienceEnabled: false, allowMaintenance: false });
  });
  vi.mocked(applyIsolation).mockImplementation(async (_id, request) => {
    expect(request.expected).toEqual(current.revisions); commit(request.draft);
    current.experienceEnabled = request.draft.experienceEnabled; current.allowMaintenance = request.draft.allowMaintenance; return copy(current);
  });
  vi.mocked(patchInstanceSettings).mockImplementation(async (_id, target, set, clear) => {
    expect(target.instanceRevision).toBe(current.revisions.instance);
    commit({ settings: [{ section: target.section, set, clear }], experienceEnabled: false, allowMaintenance: false });
    return copy(current.settings.find(s => s.section === target.section)!);
  });
});
function Workspace() {
  const [page, setPage] = useState<InstancePage>('state-isolation'); const [revision, setRevision] = useState(1);
  const updated = useCallback(() => setRevision(current.revisions.instance), []);
  return <ConfigProvider theme={{ token: { motion: false } }}><App><InstanceConfigurationProvider revision={revision} disabled={false} onUpdated={updated}>
    <StateIsolationConfigurationProvider instanceId="owner" active archived={false} onUpdated={updated} onDirtyChange={() => {}}>
      <InstanceConfigurationActions />
      <button onClick={() => setPage('state-isolation')}>Go isolation</button><button onClick={() => setPage('voice')}>Go voice</button><button onClick={() => setPage('memory-policy')}>Go memory</button>
      <RetainedAdminPage active={page === 'state-isolation'}><StateIsolationConfigurationSection instanceId="owner" active={page === 'state-isolation'} archived={false} onUpdated={updated} onDirtyChange={() => {}} /></RetainedAdminPage>
      <RetainedAdminPage active={page !== 'state-isolation'}><InstanceSettingsSection instanceId="owner" active={page !== 'state-isolation'} archived={false} page={page} onUpdated={updated} /></RetainedAdminPage>
    </StateIsolationConfigurationProvider></InstanceConfigurationProvider></App></ConfigProvider>;
}
async function save() {
  const button = screen.getByRole('button', { name: 'Save configuration' });
  await waitFor(() => expect(button).toBeEnabled(), { timeout: 5000 }); await act(async () => { fireEvent.click(button); });
  await waitFor(() => expect(screen.getByRole('region', { name: 'Instance configuration actions' })).toHaveTextContent('Configuration saved. Changes apply to the next new Run.'), { timeout: 5000 });
}

test('preset, detailed pages, manual edits, discard and reset share one atomic draft', async () => {
  render(<Workspace />);
  const preset = await screen.findByRole('combobox', { name: 'Isolation preset' });
  await waitFor(() => expect(preset).toBeEnabled()); fireEvent.mouseDown(preset);
  fireEvent.click(await screen.findByText('Stateless · new Session for each request'));
  await waitFor(() => expect(presetIsolation).toHaveBeenCalledOnce());
  fireEvent.click(screen.getByRole('button', { name: 'Go voice' }));
  expect(await screen.findByRole('switch', { name: 'Enabled' })).not.toBeChecked();
  fireEvent.click(screen.getByRole('switch', { name: 'Enabled' }));
  await screen.findAllByText('Request isolation requires Voice off.');
  expect(screen.getByRole('button', { name: 'Save configuration' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Discard configuration changes' }));
  expect(screen.getByRole('switch', { name: 'Enabled' })).toBeChecked();
  fireEvent.click(screen.getByRole('button', { name: 'Go isolation' }));
  fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Isolation preset' })); fireEvent.click(await screen.findByText('Stateless · new Session for each request'));
  fireEvent.click(screen.getByRole('button', { name: 'Go memory' }));
  expect(await screen.findByRole('switch', { name: 'Session memory' })).not.toBeChecked();
  await save();
  expect(applyIsolation).toHaveBeenCalledOnce(); expect(patchInstanceSettings).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Reset Memory policy to Definition' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Reset section' }));
  expect(applyIsolation).toHaveBeenCalledOnce();
  expect(screen.getByRole('switch', { name: 'Session memory' })).toBeChecked();
  expect(screen.getByRole('switch', { name: 'Session memory' })).toBeEnabled();
  await save(); expect(applyIsolation).toHaveBeenCalledTimes(2);
  expect(vi.mocked(applyIsolation).mock.calls[1][1].draft.settings).toEqual([{ section: 'memoryPolicy', set: {}, clear: expect.arrayContaining(['sessionMemory']) }]);
  fireEvent.click(screen.getByRole('button', { name: 'Go voice' })); expect(await screen.findByRole('switch', { name: 'Enabled' })).not.toBeChecked();
});

test.each([false, true])('mixed settings save retains disjoint edits and preserves opt-in fences (race=%s)', async race => {
  render(<Workspace />);
  await waitFor(() => expect(screen.getByRole('combobox', { name: 'Isolation preset' })).toBeEnabled());
  fireEvent.click(screen.getByRole('button', { name: 'Go voice' }));
  const voiceId = await screen.findByRole('textbox', { name: 'Voice ID' });
  await waitFor(() => expect(voiceId).toBeEnabled());
  fireEvent.change(voiceId, { target: { value: 'custom-voice' } });
  fireEvent.click(screen.getByRole('button', { name: 'Go memory' }));
  const memory = await screen.findByRole('switch', { name: 'Session memory' });
  await waitFor(() => expect(memory).toBeEnabled()); fireEvent.click(memory);
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save configuration' })).toBeEnabled(), { timeout: 5000 });
  if (race) {
    const persist = vi.mocked(patchInstanceSettings).getMockImplementation()!;
    vi.mocked(patchInstanceSettings).mockImplementation(async (...args) => {
      const result = await persist(...args); current.revisions.maintenance++; current.allowMaintenance = true; return result;
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save configuration' }));
    await screen.findByText(/1 configuration group saved before the failure/, { selector: '.ant-alert-title' });
    expect(applyIsolation).not.toHaveBeenCalled(); expect(screen.getByRole('switch', { name: 'Session memory' })).not.toBeChecked();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Refresh configuration' })).toBeEnabled());
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Refresh configuration' })); });
    await waitFor(() => expect(screen.getByRole('button', { name: 'Refresh configuration' })).toBeEnabled());
  }
  await save();
  expect(patchInstanceSettings).toHaveBeenCalledOnce(); expect(applyIsolation).toHaveBeenCalledOnce();
  expect(current.settings.find(s => s.section === 'voice')!.effective.voiceId).toBe('custom-voice');
  expect(current.settings.find(s => s.section === 'memoryPolicy')!.effective.sessionMemory).toBe(false);
  expect(current.allowMaintenance).toBe(race);
});

test('an unavailable coordinated read disables shared fields and recovers without losing independent edits', async () => {
  vi.mocked(readIsolation).mockRejectedValueOnce(new Error('Isolation read unavailable.'));
  render(<Workspace />);
  await screen.findByText('Isolation read unavailable.');
  fireEvent.click(screen.getByRole('button', { name: 'Go voice' }));
  expect(await screen.findByRole('switch', { name: 'Enabled' })).toBeDisabled();
  const voiceId = screen.getByRole('textbox', { name: 'Voice ID' });
  await waitFor(() => expect(voiceId).toBeEnabled());
  fireEvent.change(voiceId, { target: { value: 'retained' } });
  fireEvent.click(screen.getByRole('button', { name: 'Retry isolation' }));
  await waitFor(() => expect(screen.getByRole('switch', { name: 'Enabled' })).toBeEnabled());
  await waitFor(() => expect(screen.getByRole('textbox', { name: 'Voice ID' })).toHaveValue('retained'));
  expect(patchInstanceSettings).not.toHaveBeenCalled(); expect(applyIsolation).not.toHaveBeenCalled();
});
