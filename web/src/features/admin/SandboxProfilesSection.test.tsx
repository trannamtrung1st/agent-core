import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import { beforeEach, expect, it, vi } from 'vitest';
import { SandboxProfilesSection } from './SandboxProfilesSection';
import { getSandboxPlatform, saveSandboxProfile, verifySandboxProfile, checkSandboxImage, type SandboxPlatform, type SandboxProfile } from '../../services/sandboxes';

vi.mock('../../services/sandboxes', async (importOriginal) => ({...await importOriginal<typeof import('../../services/sandboxes')>(), getSandboxPlatform: vi.fn(), saveSandboxProfile: vi.fn(), verifySandboxProfile: vi.fn(),checkSandboxImage:vi.fn() }));
const profile: SandboxProfile = { id:'workbench',revision:1,name:'Workbench',description:'Coding',enabled:true,availability:'Eligible',imageReference:'sha256:'+'a'.repeat(64),initializationScript:null,cpu:1,memoryMiB:1024,pids:64,maxCommandSeconds:120,idleTtlSeconds:1800,maxLifetimeSeconds:7200,storageBytes:262144000,lastVerifiedAt:null,verificationReason:null };
const platform: SandboxPlatform = { state:'Ready',message:'Local server ready.',profiles:[profile],allowedImagePrefixes:[],localImageIdsAllowed:true,securityScope:'Trusted local demo.',storageScope:'Canonical scratch quota; no writable-layer quota.' };
beforeEach(() => { vi.mocked(getSandboxPlatform).mockReset().mockResolvedValue(structuredClone(platform)); vi.mocked(saveSandboxProfile).mockReset(); vi.mocked(verifySandboxProfile).mockReset(); vi.mocked(checkSandboxImage).mockReset(); });
const mount = () => render(<ConfigProvider theme={{ token:{ motion:false } }}><App><SandboxProfilesSection /></App></ConfigProvider>);
it('refreshes selected source eligibility after credential revocation without losing dirty edits',async () => {
  mount(); await screen.findByDisplayValue('Workbench');
  const revoked = {...profile, availability:'SourceUnavailable', availabilityMessage:'Credential was disabled. Verify again.'};
  vi.mocked(getSandboxPlatform).mockResolvedValue({...platform,profiles:[revoked]});
  fireEvent.click(screen.getByRole('button',{name:'Refresh status'}));
  await screen.findByText('Credential was disabled. Verify again.');
  await waitFor(()=>expect(screen.getByRole('button',{name:'Refresh status'})).toHaveAttribute('aria-busy','false'),{timeout:5000});
  expect(screen.queryByText(/Verified · eligible · revision/)).not.toBeInTheDocument();
  fireEvent.change(screen.getByRole('textbox',{name:'Sandbox profile description'}),{target:{value:'Keep this draft'}});
  await waitFor(()=>expect(screen.getByRole('button',{name:'Save profile'})).toBeEnabled());
  vi.mocked(getSandboxPlatform).mockResolvedValue({...platform,profiles:[{...profile,availability:'NeedsVerification',availabilityMessage:'Credential changed. Verify again.'}]});
  fireEvent.click(screen.getByRole('button',{name:'Refresh status'}));
  await screen.findByText('Credential changed. Verify again.');
  await waitFor(()=>expect(screen.getByRole('button',{name:'Refresh status'})).toHaveAttribute('aria-busy','false'),{timeout:5000});
  expect(screen.getByRole('textbox',{name:'Sandbox profile description'})).toHaveValue('Keep this draft');
  vi.mocked(getSandboxPlatform).mockResolvedValue({...platform,profiles:[{...profile,description:'External edit',revision:2}]});
  fireEvent.click(screen.getByRole('button',{name:'Refresh status'}));
  await waitFor(()=>expect(getSandboxPlatform).toHaveBeenCalledTimes(4));
  await waitFor(()=>expect(screen.getByRole('button',{name:'Refresh status'})).toHaveAttribute('aria-busy','false'),{timeout:5000});
  expect(screen.getByRole('textbox',{name:'Sandbox profile description'})).toHaveValue('Keep this draft');
  expect(screen.getByText(/Source unavailable · revision 1/)).toBeInTheDocument();
});

it('retains a conflicting revision draft and discards explicitly without granting verification',async () => {
  mount(); await screen.findByDisplayValue('Workbench');
  expect(screen.getByRole('button',{name:'Save profile'})).toBeDisabled();
  fireEvent.change(screen.getByRole('textbox',{name:'Sandbox profile name'}),{target:{value:'Changed'}});
  await waitFor(() => expect(screen.getByRole('button',{name:'Verify profile'})).toBeDisabled());
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
  const verified = {...saved,revision:2,lastVerifiedAt:'2026-10-14T00:00:00Z',verificationReason:'Image passed.'};
  vi.mocked(verifySandboxProfile).mockResolvedValue({passed:true,message:'Image passed.',profile:verified});
  vi.mocked(getSandboxPlatform).mockResolvedValue({...platform,profiles:[verified]});
  await waitFor(() => expect(screen.getByRole('button',{name:'Verify profile'})).toBeEnabled());
  fireEvent.click(screen.getByRole('button',{name:'Verify profile'}));
  await waitFor(() => expect(verifySandboxProfile).toHaveBeenCalledWith(saved));
  await screen.findByText(/Image passed.*·/);
});
it('shows a local failure and recovers without fabricating readiness',async () => {
  vi.mocked(getSandboxPlatform).mockRejectedValueOnce(new Error('Server unavailable.')); mount();
  await screen.findByText('Server unavailable.');
  expect(screen.queryByRole('button',{name:'Verify profile'})).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button',{name:'Retry'}));
  await screen.findByText('Local server ready.');
});

it('retains a failed initialization observation on the exact profile revision', async () => {
  mount(); await screen.findByDisplayValue('Workbench');
  const failed = {...profile, lastVerifiedAt:'2026-10-14T00:00:00Z', verificationReason:'Initialization failed with exit code 7. Correct the script and verify again.'};
  vi.mocked(verifySandboxProfile).mockResolvedValue({passed:false,message:failed.verificationReason!,profile:failed});
  vi.mocked(getSandboxPlatform).mockResolvedValue({...platform,profiles:[failed]});
  fireEvent.click(screen.getByRole('button', {name:'Verify profile'}));
  await screen.findByRole('status');
  expect(screen.getByRole('status')).toHaveTextContent('Initialization failed with exit code 7');
  expect(saveSandboxProfile).not.toHaveBeenCalled();
  expect(screen.getByRole('button', {name:'Save profile'})).toBeDisabled();
});

it('checks only image readiness without mutating or verifying the saved setup',async()=>{
 mount();await screen.findByDisplayValue('Workbench');
 vi.mocked(checkSandboxImage).mockResolvedValue({passed:true,message:'Image readiness passed. Initialization was not executed.',profileId:'workbench',profileRevision:1,instanceRevision:null,verifiedAt:'2026-10-14T00:00:00Z',exitCode:null,stdout:'',stderr:'',truncated:false,durationMs:1000,diagnosticId:null});
 fireEvent.click(screen.getByRole('button',{name:'Check image readiness'}));await waitFor(()=>expect(checkSandboxImage).toHaveBeenCalledWith(profile));
 expect(verifySandboxProfile).not.toHaveBeenCalled();expect(saveSandboxProfile).not.toHaveBeenCalled();
});

it('allows onboarding verification with no eligible catalog entries while the server is ready',async()=>{
 vi.mocked(getSandboxPlatform).mockResolvedValue({...platform,profiles:[{...profile,availability:'NeedsVerification'}]});mount();await screen.findByDisplayValue('Workbench');
 await waitFor(()=>expect(screen.getByRole('button',{name:'Verify profile'})).toBeEnabled());
 expect(screen.getAllByText(/Needs verification/).length).toBeGreaterThan(0);
});

it('suppresses late image-check messages after leaving the catalog',async()=>{
  let finish!: (value:Awaited<ReturnType<typeof checkSandboxImage>>)=>void;
  vi.mocked(checkSandboxImage).mockReturnValue(new Promise(resolve=>{finish=resolve;}));
  const view=mount();await screen.findByDisplayValue('Workbench');
  fireEvent.click(screen.getByRole('button',{name:'Check image readiness'}));
  await waitFor(()=>expect(checkSandboxImage).toHaveBeenCalledWith(profile));
  view.rerender(<ConfigProvider theme={{token:{motion:false}}}><App><div>Other admin page</div></App></ConfigProvider>);
  finish({passed:true,message:'Late image success',profileId:'workbench',profileRevision:1,instanceRevision:null,verifiedAt:'2026-10-15T00:00:00Z',exitCode:null,stdout:'',stderr:'',truncated:false,durationMs:1,diagnosticId:null});
  await waitFor(()=>expect(screen.getByText('Other admin page')).toBeInTheDocument());
  expect(screen.queryByText('Late image success')).not.toBeInTheDocument();
});
