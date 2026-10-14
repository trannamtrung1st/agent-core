import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import { beforeEach, expect, it, vi } from 'vitest';
import { SandboxProfilesSection } from './SandboxProfilesSection';
import { getSandboxPlatform, saveSandboxProfile, verifySandboxProfile, type SandboxPlatform, type SandboxProfile } from '../../services/sandboxes';

vi.mock('../../services/sandboxes', () => ({ getSandboxPlatform: vi.fn(), saveSandboxProfile: vi.fn(), verifySandboxProfile: vi.fn() }));
const profile: SandboxProfile = { id:'workbench',revision:1,name:'Workbench',description:'Coding',enabled:true,imageReference:'sha256:'+'a'.repeat(64),initializationScript:null,cpu:1,memoryMiB:1024,pids:64,maxCommandSeconds:120,idleTtlSeconds:1800,maxLifetimeSeconds:7200,storageBytes:262144000,lastVerifiedAt:null,verificationReason:null };
const platform: SandboxPlatform = { state:'Ready',message:'Local server ready.',profiles:[profile],allowedImagePrefixes:[],localImageIdsAllowed:true,securityScope:'Trusted local demo.',storageScope:'Canonical scratch quota; no writable-layer quota.' };
beforeEach(() => { vi.mocked(getSandboxPlatform).mockReset().mockResolvedValue(structuredClone(platform)); vi.mocked(saveSandboxProfile).mockReset(); vi.mocked(verifySandboxProfile).mockReset(); });
const mount = () => render(<ConfigProvider theme={{ token:{ motion:false } }}><App><SandboxProfilesSection /></App></ConfigProvider>);
it('retains a conflicting revision draft and discards explicitly without granting verification',async () => {
  mount(); await screen.findByDisplayValue('Workbench');
  expect(screen.getByRole('button',{name:'Save profile'})).toBeDisabled();
  fireEvent.change(screen.getByRole('textbox',{name:'Sandbox profile name'}),{target:{value:'Changed'}});
  await waitFor(() => expect(screen.getByRole('button',{name:'Verify image'})).toBeDisabled());
  vi.mocked(saveSandboxProfile).mockRejectedValueOnce(new Error('Revision changed.'));
  fireEvent.click(screen.getByRole('button',{name:'Save profile'}));
  await screen.findByText('Revision changed.');
  expect(screen.getByRole('textbox',{name:'Sandbox profile name'})).toHaveValue('Changed');
  expect(saveSandboxProfile).toHaveBeenCalledWith(expect.objectContaining({id:'workbench',revision:1,name:'Changed'}));
  fireEvent.click(screen.getByRole('button',{name:'Discard edits'}));
  expect(screen.getByRole('textbox',{name:'Sandbox profile name'})).toHaveValue('Workbench');
});
it('saves a profile revision then verifies the exact saved image',async () => {
  mount(); await screen.findByDisplayValue('Workbench');
  const saved = {...profile,revision:2,description:'Office'};
  vi.mocked(saveSandboxProfile).mockResolvedValue(saved); vi.mocked(getSandboxPlatform).mockResolvedValue({...platform,profiles:[saved]});
  fireEvent.change(screen.getByRole('textbox',{name:'Sandbox profile description'}),{target:{value:'Office'}});
  await waitFor(() => expect(screen.getByRole('button',{name:'Save profile'})).toBeEnabled());
  fireEvent.click(screen.getByRole('button',{name:'Save profile'}));
  await waitFor(() => expect(saveSandboxProfile).toHaveBeenCalled());
  await waitFor(() => expect(screen.getByRole('button',{name:'Save profile'})).toBeDisabled());
  const verified = {...saved,revision:3,lastVerifiedAt:'2026-10-14T00:00:00Z',verificationReason:'Image passed.'};
  vi.mocked(verifySandboxProfile).mockResolvedValue({passed:true,message:'Image passed.',profile:verified});
  await waitFor(() => expect(screen.getByRole('button',{name:'Verify image'})).toBeEnabled());
  fireEvent.click(screen.getByRole('button',{name:'Verify image'}));
  await waitFor(() => expect(verifySandboxProfile).toHaveBeenCalledWith(saved));
  await screen.findByText(/Image passed.*·/);
});
it('shows a local failure and recovers without fabricating readiness',async () => {
  vi.mocked(getSandboxPlatform).mockRejectedValueOnce(new Error('Server unavailable.')); mount();
  await screen.findByText('Server unavailable.');
  expect(screen.queryByRole('button',{name:'Verify image'})).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button',{name:'Retry'}));
  await screen.findByText('Local server ready.');
});
