import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { App, ConfigProvider } from 'antd';
import { InstanceSettingsSection } from './InstanceSettingsSection';
import * as api from '../../services/instanceConfiguration';
import * as modelApi from '../../services/api';

vi.mock('../../services/instanceConfiguration', () => ({ listInstanceSettings: vi.fn(), patchInstanceSettings: vi.fn() }));
vi.mock('../../services/api', () => ({ listModels: vi.fn(async () => ({ models: [] })) }));
vi.mock('./ExecutionBudgetsSection', () => ({ InstanceExecutionBudgets: () => null }));
afterEach(cleanup);
beforeEach(() => { vi.clearAllMocks(); vi.mocked(modelApi.listModels).mockResolvedValue({ defaultKey: 'host', models: [] }); });

function sections(revision = 1, voiceEnabled = true): api.SettingsSection[] {
  return [
    { section: 'voice', instanceRevision: revision, definitionId: 'agent', definitionVersion: 1,
      overrides: voiceEnabled ? {} : { enabled: false }, effective: { enabled: voiceEnabled },
      definitionDefaults: { enabled: true }, sources: { enabled: voiceEnabled ? 'definition' : 'instance' }, configurationHash: 'hash' },
    { section: 'providerPreferences', instanceRevision: revision, definitionId: 'agent', definitionVersion: 1,
      overrides: {}, effective: { speechRecognizer: voiceEnabled ? 'primary-stt' : null },
      definitionDefaults: { speechRecognizer: 'primary-stt' }, sources: { speechRecognizer: 'definition' }, configurationHash: 'hash' },
    { section: 'conversationPolicy', instanceRevision: revision, definitionId: 'agent', definitionVersion: 1,
      overrides: {}, effective: { language: 'en-US' }, definitionDefaults: { language: 'en-US' },
      sources: { language: 'definition' }, configurationHash: 'hash' }
  ];
}

async function editVoiceWithOtherDraft() {
  fireEvent.click(await screen.findByRole('button', { name: 'Provider preferences Definition default' }));
  expect(screen.getByRole('textbox', { name: 'SpeechRecognizer' })).toHaveValue('primary-stt');
  fireEvent.click(screen.getByRole('button', { name: 'Conversation Definition default' }));
  fireEvent.click(within(screen.getByRole('radiogroup', { name: 'Conversation source' })).getByText('Customize'));
  fireEvent.change(screen.getByRole('textbox', { name: 'Language' }), { target: { value: 'fr-FR' } });
  fireEvent.click(screen.getByRole('button', { name: 'Voice Definition default' }));
  fireEvent.click(within(screen.getByRole('radiogroup', { name: 'Voice source' })).getByText('Customize'));
  fireEvent.click(screen.getByRole('switch', { name: 'Enabled' }));
  fireEvent.click(screen.getByRole('button', { name: 'Save Voice' }));
}

it('refreshes dependent sections after saving while retaining unrelated drafts and expansion', async () => {
  vi.mocked(api.listInstanceSettings).mockResolvedValueOnce(sections()).mockResolvedValue(sections(2, false));
  vi.mocked(api.patchInstanceSettings).mockResolvedValue(sections(2, false)[0]);
  render(<ConfigProvider><App><InstanceSettingsSection instanceId="owner" archived={false} onUpdated={() => {}} /></App></ConfigProvider>);
  await editVoiceWithOtherDraft();
  await waitFor(() => expect(screen.getByRole('textbox', { name: 'SpeechRecognizer' })).toHaveValue(''));
  expect(screen.getByRole('textbox', { name: 'Language' })).toHaveValue('fr-FR');
  expect(screen.getByRole('button', { name: 'Save Conversation' })).toBeEnabled();
  expect(api.patchInstanceSettings).toHaveBeenCalledWith('owner', expect.objectContaining({ instanceRevision: 1 }), { enabled: false }, []);
});

it('reports a committed save when refresh fails and recovers without replaying it or losing other drafts', async () => {
  vi.mocked(api.listInstanceSettings).mockResolvedValueOnce(sections()).mockRejectedValueOnce(new Error('Read unavailable.')).mockResolvedValue(sections(2, false));
  vi.mocked(api.patchInstanceSettings).mockResolvedValue(sections(2, false)[0]);
  render(<ConfigProvider><App><InstanceSettingsSection instanceId="owner" archived={false} onUpdated={() => {}} /></App></ConfigProvider>);
  await editVoiceWithOtherDraft();
  expect(await screen.findByText(/Settings were saved\. Read unavailable\./)).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Save Voice' })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Retry settings' }));
  expect(await screen.findByRole('textbox', { name: 'Language' })).toHaveValue('fr-FR');
  expect(screen.getByRole('textbox', { name: 'SpeechRecognizer' })).toHaveValue('');
  expect(screen.getByRole('button', { name: 'Save Voice' })).toBeDisabled();
  expect(api.patchInstanceSettings).toHaveBeenCalledTimes(1);
});

it('uses host-default reasoning choices for an explicit null catalog key and orders effort semantically', async () => {
  const model = { tools: true, vision: false, structuredOutput: true, reasoning: true };
  vi.mocked(modelApi.listModels).mockResolvedValue({ defaultKey: 'host', models: [
    { ...model, key: 'host', displayName: 'Host model', supportedReasoningEfforts: ['high', 'low', 'medium'] },
    { ...model, key: 'definition', displayName: 'Definition model', supportedReasoningEfforts: ['max'] }
  ] });
  vi.mocked(api.listInstanceSettings).mockResolvedValue([{ section: 'modelDefaults', instanceRevision: 1,
    definitionId: 'agent', definitionVersion: 1, overrides: { catalogKey: null, reasoningEffort: null },
    effective: { catalogKey: null, reasoningEffort: null }, definitionDefaults: { catalogKey: 'definition', reasoningEffort: 'max' },
    sources: { catalogKey: 'instance', reasoningEffort: 'instance' }, configurationHash: 'hash' }]);
  render(<ConfigProvider><App><InstanceSettingsSection instanceId="owner" archived={false} onUpdated={() => {}} /></App></ConfigProvider>);
  fireEvent.click(await screen.findByRole('button', { name: 'Model defaults Instance override' }));
  const effort = screen.getByRole('combobox', { name: 'Reasoning effort' });
  await waitFor(() => expect(effort).toBeEnabled());
  fireEvent.mouseDown(effort);
  await screen.findByRole('option', { name: 'low' });
  const choices = Array.from(document.querySelectorAll('.ant-select-item-option-content')).map(element => element.textContent);
  expect(choices).toEqual(['low', 'medium', 'high']);
});

it('ignores a late settings refresh after switching owners', async () => {
  let finishRefresh!: (value: api.SettingsSection[]) => void;
  const pending = new Promise<api.SettingsSection[]>(resolve => { finishRefresh = resolve; });
  vi.mocked(api.listInstanceSettings).mockResolvedValueOnce(sections()).mockReturnValueOnce(pending).mockResolvedValue(sections());
  vi.mocked(api.patchInstanceSettings).mockResolvedValue(sections(2, false)[0]);
  const fixture = (instanceId: string) => <ConfigProvider><App><InstanceSettingsSection instanceId={instanceId} archived={false} onUpdated={() => {}} /></App></ConfigProvider>;
  const { rerender } = render(fixture('owner'));
  await editVoiceWithOtherDraft();
  await waitFor(() => expect(api.listInstanceSettings).toHaveBeenCalledTimes(2));
  rerender(fixture('other'));
  fireEvent.click(await screen.findByRole('button', { name: 'Provider preferences Definition default' }));
  await act(async () => { finishRefresh(sections(2, false)); await pending; });
  expect(screen.getByRole('textbox', { name: 'SpeechRecognizer' })).toHaveValue('primary-stt');
  expect(screen.getByRole('button', { name: 'Reload settings' })).toBeEnabled();
});

it('finishes the committed save after leaving and re-entering the settings tab during refresh', async () => {
  let finishRefresh!: (value: api.SettingsSection[]) => void;
  const pending = new Promise<api.SettingsSection[]>(resolve => { finishRefresh = resolve; });
  vi.mocked(api.listInstanceSettings).mockResolvedValueOnce(sections()).mockReturnValueOnce(pending).mockResolvedValue(sections(2, false));
  vi.mocked(api.patchInstanceSettings).mockResolvedValue(sections(2, false)[0]);
  const fixture = (active: boolean) => <ConfigProvider><App><InstanceSettingsSection instanceId="owner" active={active} archived={false} onUpdated={() => {}} /></App></ConfigProvider>;
  const { rerender } = render(fixture(true));
  await editVoiceWithOtherDraft();
  await waitFor(() => expect(api.listInstanceSettings).toHaveBeenCalledTimes(2));
  rerender(fixture(false)); rerender(fixture(true));
  await act(async () => { finishRefresh(sections(2, false)); await pending; });
  await waitFor(() => expect(screen.getByRole('button', { name: 'Reload settings' })).toBeEnabled());
  expect(screen.getByRole('textbox', { name: 'SpeechRecognizer' })).toHaveValue('');
  expect(screen.getByRole('textbox', { name: 'Language' })).toHaveValue('fr-FR');
});
