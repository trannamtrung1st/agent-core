import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import { beforeEach, expect, it, vi } from 'vitest';
import { SandboxProfilesSection } from './SandboxProfilesSection';
import { getSandboxPlatform, saveSandboxProfile, verifySandboxProfile, checkSandboxImage, type SandboxPlatform, type SandboxProfile } from '../../services/sandboxes';

vi.mock('../../services/sandboxes', async (importOriginal) => ({...await importOriginal<typeof import('../../services/sandboxes')>(), getSandboxPlatform: vi.fn(), saveSandboxProfile: vi.fn(), verifySandboxProfile: vi.fn(),checkSandboxImage:vi.fn() }));
vi.mock('../../services/adminApi', async (importOriginal) => ({...await importOriginal<typeof import('../../services/adminApi')>(), listCredentials:vi.fn().mockResolvedValue([])}));
const profile: SandboxProfile = { id:'workbench',revision:1,name:'Workbench',description:'Coding',enabled:true,availability:'Eligible',imageReference:'sha256:'+'a'.repeat(64),initializationScript:null,cpu:1,memoryMiB:1024,pids:64,maxCommandSeconds:120,idleTtlSeconds:1800,maxLifetimeSeconds:7200,storageBytes:262144000,lastVerifiedAt:null,verificationReason:null };
const savedProfile: SandboxProfile = {...profile,revision:2,description:'Office',availability:'NeedsVerification'};
const platform: SandboxPlatform = { state:'Ready',message:'Local server ready.',profiles:[profile],allowedImagePrefixes:[],localImageIdsAllowed:true,securityScope:'Trusted local demo.',storageScope:'Canonical scratch quota; no writable-layer quota.' };
beforeEach(() => { vi.mocked(getSandboxPlatform).mockReset().mockResolvedValue(structuredClone(platform)); vi.mocked(saveSandboxProfile).mockReset(); vi.mocked(verifySandboxProfile).mockReset(); vi.mocked(checkSandboxImage).mockReset(); });
const mount = () => render(<ConfigProvider theme={{ token:{ motion:false } }}><App><SandboxProfilesSection /></App></ConfigProvider>);
async function mountReady() {
  const view=mount();await screen.findByDisplayValue('Workbench');
  const verify=screen.getByRole('button',{name:'Verify profile'});
  await waitFor(()=>expect(verify).toBeEnabled());
  return view;
}
it('refreshes selected clean source eligibility after credential revocation',async () => {
  await mountReady();
  const refresh=screen.getByRole('button',{name:'Refresh status'});
  const revoked = {...profile, availability:'SourceUnavailable', availabilityMessage:'Credential was disabled. Verify again.'};
  vi.mocked(getSandboxPlatform).mockResolvedValue({...platform,profiles:[revoked]});
  fireEvent.click(refresh);
  await screen.findByText('Credential was disabled. Verify again.');
  await waitFor(()=>expect(refresh).toHaveAttribute('aria-busy','false'));
  expect(screen.queryByText(/Verified · eligible · revision/)).not.toBeInTheDocument();
});

it('refreshes source status while retaining dirty fields across credential and configuration changes',async () => {
  const revoked={...profile,availability:'SourceUnavailable'};
  vi.mocked(getSandboxPlatform).mockResolvedValue({...platform,profiles:[revoked]});
  await mountReady();
  const refresh=screen.getByRole('button',{name:'Refresh status'});
  const description=screen.getByRole('textbox',{name:'Sandbox profile description'});
  const save=screen.getByRole('button',{name:'Save profile'});
  fireEvent.change(description,{target:{value:'Keep this draft'}});
  await waitFor(()=>expect(save).toBeEnabled());
  vi.mocked(getSandboxPlatform).mockResolvedValue({...platform,profiles:[{...profile,availability:'NeedsVerification',availabilityMessage:'Credential changed. Verify again.'}]});
  fireEvent.click(refresh);
  await screen.findByText('Credential changed. Verify again.');
  await waitFor(()=>expect(refresh).toHaveAttribute('aria-busy','false'));
  expect(description).toHaveValue('Keep this draft');
  vi.mocked(getSandboxPlatform).mockResolvedValue({...platform,profiles:[{...profile,description:'External edit',revision:2}]});
  fireEvent.click(refresh);
  await waitFor(()=>expect(getSandboxPlatform).toHaveBeenCalledTimes(3));
  await waitFor(()=>expect(refresh).toHaveAttribute('aria-busy','false'));
  expect(description).toHaveValue('Keep this draft');
  expect(screen.getByText(/Source unavailable · revision 1/)).toBeInTheDocument();
});

it('retains a conflicting revision draft and discards explicitly without granting verification',async () => {
  await mountReady();
  const save=screen.getByRole('button',{name:'Save profile'});
  const verify=screen.getByRole('button',{name:'Verify profile'});
  const name=screen.getByRole('textbox',{name:'Sandbox profile name'});
  const discard=screen.getByRole('button',{name:'Discard edits'});
  expect(save).toBeDisabled();
  fireEvent.change(name,{target:{value:'Changed'}});
  await waitFor(() => expect(verify).toBeDisabled());
  vi.mocked(saveSandboxProfile).mockRejectedValueOnce(new Error('Revision changed.'));
  await waitFor(()=>expect(save).toBeEnabled());
  fireEvent.click(save);
  await screen.findByText('Revision changed.');
  expect(name).toHaveValue('Changed');
  expect(saveSandboxProfile).toHaveBeenCalledWith(expect.objectContaining({id:'workbench',revision:1,name:'Changed'}));
  await waitFor(()=>expect(discard).toBeEnabled());
  fireEvent.click(discard);
  await screen.findByDisplayValue('Workbench');
  expect(verifySandboxProfile).not.toHaveBeenCalled();
});
it('saves the next profile revision and keeps it unverified with a clean form',async () => {
  await mountReady();
  const save=screen.getByRole('button',{name:'Save profile'});
  const verify=screen.getByRole('button',{name:'Verify profile'});
  vi.mocked(saveSandboxProfile).mockResolvedValue(savedProfile); vi.mocked(getSandboxPlatform).mockResolvedValue({...platform,profiles:[savedProfile]});
  fireEvent.change(screen.getByRole('textbox',{name:'Sandbox profile description'}),{target:{value:'Office'}});
  await waitFor(() => expect(save).toBeEnabled());
  fireEvent.click(save);
  await screen.findByText('Needs verification · revision 2');
  expect(saveSandboxProfile).toHaveBeenCalledWith(expect.objectContaining({id:'workbench',revision:1,description:'Office'}));
  await waitFor(() => expect(save).toBeDisabled());
  await waitFor(() => expect(verify).toBeEnabled());
  expect(verifySandboxProfile).not.toHaveBeenCalled();
});
it('verifies the exact saved revision without appending another configuration revision',async () => {
  vi.mocked(getSandboxPlatform).mockResolvedValue({...platform,profiles:[savedProfile]});
  await mountReady();
  const verify=screen.getByRole('button',{name:'Verify profile'});
  const verified = {...savedProfile,availability:'Eligible',lastVerifiedAt:'2026-10-14T00:00:00Z',verificationReason:'Image passed.'};
  vi.mocked(verifySandboxProfile).mockResolvedValue({passed:true,message:'Image passed.',profile:verified});
  vi.mocked(getSandboxPlatform).mockResolvedValue({...platform,profiles:[verified]});
  fireEvent.click(verify);
  await waitFor(() => expect(verifySandboxProfile).toHaveBeenCalledWith(savedProfile));
  await screen.findByText(/Image passed.*·/);
  expect(screen.getByText('Verified · eligible · revision 2')).toBeInTheDocument();
  expect(saveSandboxProfile).not.toHaveBeenCalled();
});
it('shows a local failure and recovers without fabricating readiness',async () => {
  vi.mocked(getSandboxPlatform).mockRejectedValueOnce(new Error('Server unavailable.')); mount();
  await screen.findByText('Server unavailable.');
  expect(screen.queryByRole('button',{name:'Verify profile'})).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button',{name:'Retry'}));
  await screen.findByText('Local server ready.');
});

it('retains a failed initialization observation on the exact profile revision', async () => {
  await mountReady();
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
 await mountReady();
 vi.mocked(checkSandboxImage).mockResolvedValue({passed:true,message:'Image readiness passed. Initialization was not executed.',profileId:'workbench',profileRevision:1,instanceRevision:null,verifiedAt:'2026-10-14T00:00:00Z',exitCode:null,stdout:'',stderr:'',truncated:false,durationMs:1000,diagnosticId:null});
 const check=screen.getByRole('button',{name:'Check image readiness'});await waitFor(()=>expect(check).toBeEnabled());
 fireEvent.click(check);await waitFor(()=>expect(checkSandboxImage).toHaveBeenCalledWith(profile));
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
  const view=await mountReady();const check=screen.getByRole('button',{name:'Check image readiness'});await waitFor(()=>expect(check).toBeEnabled());
  fireEvent.click(check);
  await waitFor(()=>expect(checkSandboxImage).toHaveBeenCalledWith(profile));
  view.rerender(<ConfigProvider theme={{token:{motion:false}}}><App><div>Other admin page</div></App></ConfigProvider>);
  await act(async()=>finish({passed:true,message:'Late image success',profileId:'workbench',profileRevision:1,instanceRevision:null,verifiedAt:'2026-10-15T00:00:00Z',exitCode:null,stdout:'',stderr:'',truncated:false,durationMs:1,diagnosticId:null}));
  await waitFor(()=>expect(screen.getByText('Other admin page')).toBeInTheDocument());
  expect(screen.queryByText('Late image success')).not.toBeInTheDocument();
});
