import { App, ConfigProvider } from 'antd';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { InstanceSkillsSection } from './InstanceSkillsSection';
import { listInstanceSkills, inspectInstanceSkill, createInstanceSkill, updateInstanceSkill, toggleInstanceSkill, customizeInstanceSkill, deleteInstanceSkill, type InstanceSkill } from '../../services/instanceSkills';
vi.mock('../../services/instanceSkills', () => ({ listInstanceSkills: vi.fn(), inspectInstanceSkill: vi.fn(), createInstanceSkill: vi.fn(), updateInstanceSkill: vi.fn(), toggleInstanceSkill: vi.fn(), customizeInstanceSkill: vi.fn(), deleteInstanceSkill: vi.fn() }));
const definition: InstanceSkill = { key: 'definition:review', origin: 'Definition', name: 'Review', description: 'Reusable review', procedure: 'UPSTREAM', projection: 'OnDemand', enabled: true, requiredCapabilities: ['workspace.read'], revision: 1, definitionVersion: 2, sourceDefinitionId: null, sourceDefinitionVersion: null, sourceDefinitionSkillId: null, missingCapabilities: [] };
const local: InstanceSkill = { ...definition, key: 'instance:00000000-0000-0000-0000-000000000001', origin: 'Instance', description: 'Independent review', procedure: 'LOCAL', projection: 'Always', definitionVersion: null, missingCapabilities: ['workspace.read'] };
const view = (archived = false) => <ConfigProvider><App><InstanceSkillsSection instanceId="owner" archived={archived} /></App></ConfigProvider>;
beforeEach(() => { vi.resetAllMocks(); vi.mocked(listInstanceSkills).mockResolvedValue([definition, local]); vi.mocked(inspectInstanceSkill).mockImplementation(async (_id, key) => key === definition.key ? definition : local); vi.mocked(toggleInstanceSkill).mockResolvedValue(definition); vi.mocked(customizeInstanceSkill).mockResolvedValue(local); vi.mocked(deleteInstanceSkill).mockResolvedValue(local); vi.mocked(createInstanceSkill).mockResolvedValue(local); });
describe('Instance Skills ownership', () => {
  it('groups both same-name origins, warns about missing capabilities, and toggles with revision', async () => {
    render(view()); await screen.findByText('Reusable review');
    expect(within(screen.getByRole('region', { name: 'Definition Skills' })).getByText('Review')).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Instance Skills' })).getByText('Review')).toBeInTheDocument();
    expect(screen.getByText('Missing authority: workspace.read')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('switch', { name: 'Enable Definition Skill Review' }));
    await waitFor(() => expect(toggleInstanceSkill).toHaveBeenCalledWith('owner', definition, false));
  });
  it('keeps Definition content read-only and confirms explicit independent customization', async () => {
    render(view()); await screen.findByText('Reusable review'); fireEvent.click(screen.getByRole('button', { name: 'Inspect' }));
    await screen.findByText('UPSTREAM'); expect(screen.getByLabelText('Procedure')).not.toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Customize' }));
    const dialog = await screen.findByRole('dialog'); expect(within(dialog).getByText(/independent Instance Skill/)).toBeInTheDocument();
    expect(customizeInstanceSkill).not.toHaveBeenCalled(); fireEvent.click(within(dialog).getByRole('button', { name: 'Customize' }));
    await waitFor(() => expect(customizeInstanceSkill).toHaveBeenCalledWith('owner', definition));
  });
  it('retains an editable draft on conflicts and confirms local deletion', async () => {
    vi.mocked(updateInstanceSkill).mockRejectedValue(new Error('Skill revision is stale.'));
    render(view()); await screen.findByText('Independent review'); fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
    const procedure = await screen.findByLabelText('Procedure'); await waitFor(() => expect(procedure).toBeVisible()); fireEvent.change(procedure, { target: { value: 'EDITED' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Skill' }));
    await waitFor(() => expect(updateInstanceSkill).toHaveBeenCalled()); expect(procedure).toHaveValue('EDITED');
    await screen.findByRole('button', { name: 'Retry Skills' });
    fireEvent.click(screen.getByRole('button', { name: 'Cancel editing' }));
    fireEvent.click(screen.getByRole('button', { name: 'Delete' })); expect(deleteInstanceSkill).not.toHaveBeenCalled();
    fireEvent.click(await screen.findByRole('button', { name: 'Delete Skill' }));
    await waitFor(() => expect(deleteInstanceSkill).toHaveBeenCalledWith('owner', local));
  });
  it('creates an independent Skill with explicit projection and required capabilities', async () => {
    render(view()); await screen.findByText('Reusable review');
    const opener = screen.getByRole('button', { name: 'New Instance Skill' }); opener.focus(); fireEvent.click(opener);
    await waitFor(() => expect(screen.getByLabelText('Skill name')).toBeVisible()); fireEvent.change(screen.getByLabelText('Skill name'), { target: { value: 'Accounting' } });
    fireEvent.change(screen.getByLabelText('Description'), { target: { value: 'Check totals' } });
    fireEvent.change(screen.getByLabelText('Procedure'), { target: { value: 'Verify totals' } });
    fireEvent.change(screen.getByLabelText('Required capabilities'), { target: { value: 'workspace.read' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save Skill' }));
    await waitFor(() => expect(createInstanceSkill).toHaveBeenCalledWith('owner', expect.objectContaining({ name: 'Accounting', projection: 'OnDemand', enabled: true, requiredCapabilities: ['workspace.read'] })));
    await waitFor(() => expect(opener).toHaveFocus());
  });
  it('keeps failures distinct from empty ownership states and retries', async () => {
    vi.mocked(listInstanceSkills).mockRejectedValueOnce(new Error('Unavailable')).mockResolvedValueOnce([]);
    render(view()); expect(screen.getByLabelText('Loading Skills')).toBeInTheDocument(); await screen.findByRole('button', { name: 'Retry Skills' });
    expect(screen.queryByText('This Definition has no reusable Skills.')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Retry Skills' })); await screen.findByText('This Definition has no reusable Skills.');
    expect(screen.getByText(/Create a local Skill or Customize/)).toBeInTheDocument();
  });
  it('disables mutation on archived instances', async () => {
    render(view(true)); await screen.findByText('Reusable review');
    expect(screen.getByRole('button', { name: 'New Instance Skill' })).toBeDisabled(); expect(screen.getByRole('button', { name: 'Customize' })).toBeDisabled();
    expect(screen.getByRole('switch', { name: 'Enable Instance Skill Review' })).toBeDisabled();
    const button = within(screen.getByRole('region', { name: 'Instance Skills' })).getByRole('button', { name: 'Inspect' });
    button.focus(); fireEvent.click(button); await screen.findByRole('region', { name: 'Instance Skill content' });
    expect(screen.getByRole('heading', { name: 'Review', level: 4 })).toHaveFocus();
    fireEvent.click(screen.getByRole('button', { name: 'Close inspection' }));
    await waitFor(() => expect(button).toHaveFocus());
  });
});
