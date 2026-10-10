import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
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
  fireEvent.click(await screen.findByRole('button', { name: 'Provider preferences' }));
  expect(screen.getByRole('textbox', { name: 'Speech recognizer' })).toHaveValue('primary-stt');
  fireEvent.click(screen.getByRole('button', { name: 'Conversation' }));
  fireEvent.click(screen.getByRole('button', { name: 'Customize Conversation' }));
  fireEvent.change(screen.getByRole('textbox', { name: 'Language' }), { target: { value: 'fr-FR' } });
  fireEvent.click(screen.getByRole('button', { name: 'Voice' }));
  fireEvent.click(screen.getByRole('button', { name: 'Customize Voice' }));
  fireEvent.click(screen.getByRole('switch', { name: 'Enabled' }));
  fireEvent.click(screen.getByRole('button', { name: 'Save Voice' }));
}

it('refreshes dependent sections after saving while retaining unrelated drafts and expansion', async () => {
  vi.mocked(api.listInstanceSettings).mockResolvedValueOnce(sections()).mockResolvedValue(sections(2, false));
  vi.mocked(api.patchInstanceSettings).mockResolvedValue(sections(2, false)[0]);
  render(<ConfigProvider><App><InstanceSettingsSection instanceId="owner" archived={false} onUpdated={() => {}} /></App></ConfigProvider>);
  await editVoiceWithOtherDraft();
  await waitFor(() => expect(screen.getByRole('textbox', { name: 'Speech recognizer' })).toHaveValue(''));
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
  expect(screen.getByRole('textbox', { name: 'Speech recognizer' })).toHaveValue('');
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
  fireEvent.click(await screen.findByRole('button', { name: /^Model defaults/ }));
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
  fireEvent.click(await screen.findByRole('button', { name: 'Provider preferences' }));
  await act(async () => { finishRefresh(sections(2, false)); await pending; });
  expect(screen.getByRole('textbox', { name: 'Speech recognizer' })).toHaveValue('primary-stt');
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
  expect(screen.getByRole('textbox', { name: 'Speech recognizer' })).toHaveValue('');
  expect(screen.getByRole('textbox', { name: 'Language' })).toHaveValue('fr-FR');
});


it('customizes without writing and discards a local draft back to Definition defaults', async () => {
  vi.mocked(api.listInstanceSettings).mockResolvedValue(sections());
  render(<ConfigProvider><App><InstanceSettingsSection instanceId="owner" archived={false} onUpdated={() => {}} /></App></ConfigProvider>);
  fireEvent.click(await screen.findByRole('button', { name: 'Conversation' }));
  const language = screen.getByRole('textbox', { name: 'Language' });
  expect(language).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Customize Conversation' }));
  expect(language).toBeEnabled();
  expect(screen.getByRole('button', { name: 'Save Conversation' })).toBeDisabled();
  expect(api.patchInstanceSettings).not.toHaveBeenCalled();
  fireEvent.change(language, { target: { value: 'fr-FR' } });
  expect(screen.getByRole('button', { name: /^Conversation.*Unsaved changes/ })).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Discard Conversation changes' }));
  expect(language).toHaveValue('en-US');
  expect(language).toBeDisabled();
  expect(screen.getByRole('button', { name: 'Customize Conversation' })).toBeVisible();
  expect(api.patchInstanceSettings).not.toHaveBeenCalled();
});

it('shows override provenance only in focusable tooltips and removes it after reset', async () => {
  const inherited = sections();
  const saved = sections(2, false);
  vi.mocked(api.listInstanceSettings).mockResolvedValueOnce(inherited).mockResolvedValueOnce(saved).mockResolvedValue(inherited);
  vi.mocked(api.patchInstanceSettings).mockResolvedValue(saved[0]);
  render(<ConfigProvider><App><InstanceSettingsSection instanceId="owner" archived={false} onUpdated={() => {}} /></App></ConfigProvider>);
  fireEvent.click(await screen.findByRole('button', { name: 'Voice' }));
  expect(screen.queryByText('Definition default')).not.toBeInTheDocument();
  expect(screen.queryByText('Using the selected Definition’s defaults.')).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Enabled: Instance override' })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Customize Voice' }));
  fireEvent.click(screen.getByRole('switch', { name: 'Enabled' }));
  expect(screen.getByRole('button', { name: 'Enabled: Unsaved Instance override' })).toBeEnabled();
  fireEvent.click(screen.getByRole('button', { name: 'Save Voice' }));
  const indicator = await screen.findByRole('button', { name: 'Enabled: Instance override' });
  fireEvent.focus(indicator);
  expect(await screen.findByRole('tooltip')).toHaveTextContent('Instance override. Definition default: true.');
  fireEvent.blur(indicator);
  fireEvent.click(screen.getByRole('button', { name: 'Reset Enabled' }));
  expect(screen.getByRole('switch', { name: 'Enabled' })).toBeChecked();
  expect(screen.getByRole('button', { name: 'Enabled: Will inherit on save' })).toBeEnabled();
  fireEvent.click(screen.getByRole('button', { name: 'Save Voice' }));
  await waitFor(() => expect(screen.queryByRole('button', { name: 'Enabled: Instance override' })).not.toBeInTheDocument());
  expect(api.patchInstanceSettings).toHaveBeenLastCalledWith('owner', expect.objectContaining({ instanceRevision: 2 }), {}, ['enabled']);
});

it('explains published restrictions before saving while allowing independent toggles', async () => {
  const fixture: api.SettingsSection = { section: 'behaviorPolicy', instanceRevision: 1, definitionId: 'agent', definitionVersion: 1,
    overrides: {}, effective: { acknowledgeInterruption: true, avoidUnsupportedClaims: true },
    definitionDefaults: { acknowledgeInterruption: true, avoidUnsupportedClaims: true },
    sources: { acknowledgeInterruption: 'definition', avoidUnsupportedClaims: 'definition' }, configurationHash: 'hash',
    constraints: { avoidUnsupportedClaims: { requiredBoolean: true, minimum: null, maximum: null, reason: 'Avoid unsupported claims is required by the selected Definition and cannot be disabled for this Instance.' } } };
  vi.mocked(api.listInstanceSettings).mockResolvedValue([fixture]);
  render(<ConfigProvider><App><InstanceSettingsSection instanceId="owner" archived={false} onUpdated={() => {}} /></App></ConfigProvider>);
  fireEvent.click(await screen.findByRole('button', { name: 'Behavior' }));
  fireEvent.click(screen.getByRole('button', { name: 'Customize Behavior' }));
  expect(screen.getByRole('switch', { name: 'Avoid unsupported claims' })).toBeDisabled();
  const restriction = screen.getByRole('button', { name: 'Avoid unsupported claims: Definition restriction' });
  fireEvent.focus(restriction);
  expect(await screen.findByRole('tooltip')).toHaveTextContent(fixture.constraints!.avoidUnsupportedClaims.reason);
  fireEvent.click(screen.getByRole('switch', { name: 'Acknowledge interruption' }));
  expect(screen.getByRole('button', { name: 'Save Behavior' })).toBeEnabled();
  expect(api.patchInstanceSettings).not.toHaveBeenCalled();
});
