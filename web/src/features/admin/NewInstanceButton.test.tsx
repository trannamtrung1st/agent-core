import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { NewInstanceButton } from './AdminGlobalPages';
import { createAdminAgentInstance, getAdminDefinitionVersion, getSelfManagement, saveSelfManagement } from '../../services/adminApi';
import { navigateToAppPath } from '../../app/appRoute';
import { defaultManagement, inheritedManagement, managementAreas } from './SelfManagementFields';
import type { DefinitionInventoryGroup } from './adminPresentation';

vi.mock('../../services/adminApi', () => ({ createAdminAgentInstance: vi.fn(), getAdminDefinitionVersion: vi.fn(),
  getSelfManagement: vi.fn(), saveSelfManagement: vi.fn() }));
vi.mock('../../app/appRoute', async original => ({ ...await original<typeof import('../../app/appRoute')>(), navigateToAppPath: vi.fn() }));
const groups: DefinitionInventoryGroup[] = [{ definitionId: 'examiner', logicalName: 'Examiner', defaultPersona: 'Examiner',
  latestVersion: 2, latestStatus: 'published', draftCount: 0, draftOnly: false,
  versions: [1, 2].map(version => ({ definitionId: 'examiner', version, source: 'durable', status: 'published', displayName: 'Examiner' })) }];
const definition = { identity: { name: 'Examiner', role: 'Tutor', description: 'Practice', tone: 'Calm' },
  selfManagement: { ...defaultManagement, mode: 'Managed', scopes: ['Settings'], managedAutoApply: ['ConversationPreferences'],
    builtInSkills: { configurationAssistance: false, harnessAuthoring: true } } };
const savedView = { instanceRevision: 2, policyRevision: 2, source: 'per-field', effective: defaultManagement, definitionDefaults: defaultManagement, overrides: null };
const created = { instanceId: 'owner', definitionId: 'examiner', activeVersion: 2, lifecycle: 'Active', revision: 1, personaRevision: 1 };
async function open() {
  render(<App><NewInstanceButton groups={groups} /></App>);
  fireEvent.click(screen.getByRole('button', { name: 'New instance' }));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Create instance' })).toBeEnabled());
  fireEvent.click(screen.getByText('Self-management (optional)'));
  return within(screen.getByRole('dialog', { name: 'New instance' }));
}
async function select(name: string, label: string) {
  fireEvent.mouseDown(screen.getByRole('combobox', { name }));
  const option = await screen.findByText(label, { selector: '.ant-select-item-option-content' });
  await act(async () => { fireEvent.click(option); });
}
describe('New Instance self-management parity', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(getAdminDefinitionVersion).mockReset().mockResolvedValue(definition);
    vi.mocked(createAdminAgentInstance).mockResolvedValue(created);
    vi.mocked(saveSelfManagement).mockReset().mockResolvedValue(savedView);
    vi.mocked(getSelfManagement).mockResolvedValue({ ...savedView, instanceRevision: 4 });
  });
  it('shows all areas and exact inherited choices without writing a policy on creation', async () => {
    const dialog = await open();
    for (const area of managementAreas) expect(dialog.getByRole('checkbox', { name: area.label })).toBeInTheDocument();
    expect(dialog.getByRole('checkbox', { name: 'Settings' })).toBeChecked();
    expect(dialog.getByRole('checkbox', { name: 'Conversation preferences' })).toBeChecked();
    expect(dialog.getByText('Inherit (Disabled)')).toBeInTheDocument();
    await act(async () => { fireEvent.click(dialog.getByRole('button', { name: 'Create instance' })); });
    await waitFor(() => expect(navigateToAppPath).toHaveBeenCalled());
    expect(createAdminAgentInstance).toHaveBeenCalledWith('examiner', 2, null);
    expect(saveSelfManagement).not.toHaveBeenCalled();
  });
  it('saves sparse automatic and procedure choices even when mode stays inherited', async () => {
    const dialog = await open();
    fireEvent.click(dialog.getByRole('checkbox', { name: 'Skills' }));
    fireEvent.click(dialog.getByRole('checkbox', { name: 'Skill create/update/enable' }));
    await select('Harness authoring procedure', 'Disabled');
    await act(async () => { fireEvent.click(dialog.getByRole('button', { name: 'Create instance' })); });
    await waitFor(() => expect(saveSelfManagement).toHaveBeenCalledWith('owner', {
      ...inheritedManagement, expectedRevision: 1, scopes: ['Settings', 'Skills'],
      managedAutoApply: ['ConversationPreferences', 'SkillChanges'], harnessAuthoring: false
    }));
  });
  it('keeps Off explicit and preserves automatic choices across mode toggles', async () => {
    const dialog = await open();
    fireEvent.click(dialog.getByRole('checkbox', { name: 'New text resources' }));
    await select('Self-management mode', 'Off');
    expect(dialog.queryByRole('checkbox', { name: 'New text resources' })).not.toBeInTheDocument();
    await select('Self-management mode', 'Managed');
    expect(dialog.getByRole('checkbox', { name: 'New text resources' })).toBeChecked();
    await select('Self-management mode', 'Off');
    await act(async () => { fireEvent.click(dialog.getByRole('button', { name: 'Create instance' })); });
    await waitFor(() => expect(saveSelfManagement).toHaveBeenCalledWith('owner', expect.objectContaining({
      mode: 'Disabled', managedAutoApply: ['ConversationPreferences', 'TextResources'], scopes: null
    })));
  });
  it('retains a failed policy draft and retries the same created Instance with a fresh revision', async () => {
    vi.mocked(saveSelfManagement).mockRejectedValueOnce(new Error('Stale policy revision'));
    const dialog = await open();
    fireEvent.click(dialog.getByRole('checkbox', { name: 'Skills' }));
    await act(async () => { fireEvent.click(dialog.getByRole('button', { name: 'Create instance' })); });
    expect(await screen.findByText('Instance created; self-management was not saved')).toBeInTheDocument();
    expect(dialog.getByRole('combobox', { name: 'Definition' })).toBeDisabled();
    expect(dialog.getByRole('checkbox', { name: 'Skills' })).toBeChecked();
    expect(navigateToAppPath).not.toHaveBeenCalled();
    await act(async () => { fireEvent.click(dialog.getByRole('button', { name: 'Retry self-management' })); });
    await waitFor(() => expect(navigateToAppPath).toHaveBeenCalled());
    expect(createAdminAgentInstance).toHaveBeenCalledTimes(1);
    expect(saveSelfManagement).toHaveBeenLastCalledWith('owner', expect.objectContaining({ expectedRevision: 4, scopes: ['Settings', 'Skills'] }));
  });
  it('reloads defaults and clears custom overrides when changing the selected publication', async () => {
    const dialog = await open();
    fireEvent.click(dialog.getByRole('checkbox', { name: 'Skills' }));
    vi.mocked(getAdminDefinitionVersion).mockResolvedValue({ ...definition, selfManagement: { ...defaultManagement, scopes: ['Resources'] } });
    await select('Published version', 'v1 · Durable · Published');
    await waitFor(() => expect(dialog.getByRole('button', { name: 'Create instance' })).toBeEnabled());
    expect(dialog.getByRole('checkbox', { name: 'Resources' })).toBeChecked();
    expect(dialog.getByRole('checkbox', { name: 'Skills' })).not.toBeChecked();
    await act(async () => { fireEvent.click(dialog.getByRole('button', { name: 'Create instance' })); });
    await waitFor(() => expect(createAdminAgentInstance).toHaveBeenCalledWith('examiner', 1, null));
    await waitFor(() => expect(navigateToAppPath).toHaveBeenCalled());
    expect(saveSelfManagement).not.toHaveBeenCalled();
  });
  it('blocks creation after a defaults load failure and recovers on Retry', async () => {
    vi.mocked(getAdminDefinitionVersion).mockRejectedValueOnce(new Error('Defaults unavailable'));
    render(<App><NewInstanceButton groups={groups} /></App>);
    fireEvent.click(screen.getByRole('button', { name: 'New instance' }));
    expect(await screen.findByText('Defaults unavailable')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create instance' })).toBeDisabled();
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Retry' })));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Create instance' })).toBeEnabled());
  });
});
