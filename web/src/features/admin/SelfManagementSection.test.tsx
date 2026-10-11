import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SelfManagementSection } from './SelfManagementSection';
import { getSelfManagement, saveSelfManagement, type SelfManagementView } from '../../services/adminApi';

vi.mock('../../services/adminApi', () => ({ getSelfManagement: vi.fn(), saveSelfManagement: vi.fn() }));
const off = { mode: 'Disabled' as const, scopes: [], sources: [], eligibleTools: [], frozen: false, managedAutoApply: null, builtInSkills: null };
const initial: SelfManagementView = { instanceRevision: 1, policyRevision: 1, source: 'definition',
  effective: off, definitionDefaults: off, overrides: null };
function mount(archived = false) { render(<App><SelfManagementSection instanceId="owner" archived={archived} onUpdated={vi.fn()} /></App>); }
async function chooseMode(label: string) {
  fireEvent.mouseDown(screen.getByRole('combobox', {name: 'Self-management mode'}));
  fireEvent.click(document.querySelectorAll('.ant-select-item-option')[['Inherit (Off)', 'Off', 'Assisted', 'Managed'].indexOf(label)]);
}
describe('Agent self-management authority panel', () => {
  beforeEach(() => { vi.mocked(getSelfManagement).mockReset().mockResolvedValue(initial); vi.mocked(saveSelfManagement).mockReset(); });
  it('inherits Off and disables mutations for archived Instances', async () => {
    mount(true); expect(await screen.findByText('Definition defaults · policy revision 1 · Instance revision 1')).toBeVisible();
    expect(screen.getByRole('combobox', {name: 'Self-management mode'})).toBeDisabled();
    expect(screen.queryByRole('button', {name: 'Freeze self-management'})).not.toBeInTheDocument();
  });
  it('saves an explicit sparse grant without increasing inherited automatic allowances', async () => {
    vi.mocked(saveSelfManagement).mockResolvedValue(initial); mount();
    await screen.findByText('Definition defaults · policy revision 1 · Instance revision 1');
    await chooseMode('Managed'); fireEvent.click(screen.getByRole('button', {name: 'Customize areas'}));
    fireEvent.click(screen.getByRole('checkbox', {name: /^Settings$/}));
    fireEvent.click(screen.getByRole('button', {name: 'Save self-management'}));
    await waitFor(() => expect(saveSelfManagement).toHaveBeenCalledWith('owner', {expectedRevision:1, mode:'Managed', scopes:['Settings'], managedAutoApply:null, configurationAssistance:null, harnessAuthoring:null}));
  });
  it('keeps a draft after a stale save and refreshes only the revision before an explicit retry', async () => {
    vi.mocked(saveSelfManagement).mockRejectedValueOnce(new Error('Instance revision changed')).mockResolvedValue(initial);
    vi.mocked(getSelfManagement).mockResolvedValueOnce(initial).mockResolvedValueOnce({...initial, instanceRevision:4});
    mount(); await screen.findByText('Definition defaults · policy revision 1 · Instance revision 1'); await chooseMode('Assisted');
    fireEvent.click(screen.getByRole('button', {name: 'Save self-management'}));
    expect(await screen.findByText('Instance revision changed')).toBeVisible();
    fireEvent.click(screen.getByRole('button', {name: 'Refresh policy'}));
    await screen.findByText('Definition defaults · policy revision 1 · Instance revision 4');
    expect(saveSelfManagement).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', {name: 'Save self-management'}));
    await waitFor(() => expect(saveSelfManagement).toHaveBeenLastCalledWith('owner', expect.objectContaining({mode:'Assisted', expectedRevision:4})));
  });
  it('requires deliberate Admin confirmation to remove a frozen lock', async () => {
    vi.mocked(getSelfManagement).mockResolvedValue({...initial, effective:{...off,frozen:true}});
    vi.mocked(saveSelfManagement).mockResolvedValue(initial); mount();
    fireEvent.click(await screen.findByRole('button', {name:'Unfreeze self-management'}));
    expect(saveSelfManagement).not.toHaveBeenCalled();
    fireEvent.click(await screen.findByRole('button', {name:/^Unfreeze$/}));
    await waitFor(() => expect(saveSelfManagement).toHaveBeenCalledWith('owner', expect.objectContaining({frozen:false,expectedRevision:1})));
  });
  it.each([false, true])('changes only the persisted lock with unsaved mode and area edits (frozen=%s)', async frozen => {
    const stored = {...initial, effective:{...off, mode:'Assisted' as const, scopes:['Settings' as const], frozen},
      source:'instance' as const, overrides:{mode:{value:'Assisted' as const}, scopes:{value:['Settings' as const]}, autoApply:null, configurationAssistance:null, harnessAuthoring:null}};
    vi.mocked(getSelfManagement).mockResolvedValue(stored);
    vi.mocked(saveSelfManagement).mockResolvedValue({...stored, instanceRevision:2, effective:{...stored.effective, frozen:!frozen}});
    mount(); await screen.findByText('Instance policy · policy revision 1 · Instance revision 1');
    await chooseMode('Managed'); fireEvent.click(screen.getByRole('checkbox', {name:/^Skills$/}));
    fireEvent.click(screen.getByRole('button', {name:frozen ? 'Unfreeze self-management' : 'Freeze self-management'}));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(dialog.querySelector('.ant-btn-primary')!);
    await waitFor(() => expect(saveSelfManagement).toHaveBeenCalledWith('owner', expect.objectContaining({
      expectedRevision:1, mode:'Assisted', scopes:['Settings'], frozen:!frozen
    })));
    await screen.findByText('Instance policy · policy revision 1 · Instance revision 2');
    fireEvent.click(screen.getByRole('button', {name:'Save self-management'}));
    await waitFor(() => expect(saveSelfManagement).toHaveBeenLastCalledWith('owner', expect.objectContaining({mode:'Managed',scopes:['Settings','Skills'],expectedRevision:2})));
  });
});
