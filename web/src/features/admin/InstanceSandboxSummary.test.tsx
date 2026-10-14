import { act,fireEvent,render,screen,waitFor } from '@testing-library/react';
import { App,ConfigProvider } from 'antd';
import { beforeEach,expect,it,vi } from 'vitest';
import { InstanceSandboxSummary } from './InstanceSandboxSummary';
import { getInstanceSandbox,verifyInstanceSandbox,type SandboxVerification } from '../../services/sandboxes';
vi.mock('../../services/sandboxes',()=>({getInstanceSandbox:vi.fn(),verifyInstanceSandbox:vi.fn()}));
const profile={id:'fixture',revision:2,name:'Fixture',description:'',enabled:true,imageReference:'sha256:'+'a'.repeat(64),initializationScript:'echo Profile',cpu:1,memoryMiB:1024,pids:64,maxCommandSeconds:120,idleTtlSeconds:1800,maxLifetimeSeconds:7200,storageBytes:262144000,lastVerifiedAt:null,verificationReason:null};
const config={profile,instanceRevision:4,instanceInitializationScript:'echo Instance',policy:{defaultProfileId:'fixture',allowedProfileIds:['fixture']},state:'Ready',message:'Authenticated.',verification:null};
const evidence:SandboxVerification={passed:false,message:'Instance initialization failed with exit code 7.',profileId:'fixture',profileRevision:2,instanceRevision:4,verifiedAt:'2026-10-14T00:00:00Z',exitCode:7,stdout:'Hello, world!',stderr:'bounded-error',truncated:false,durationMs:1000,diagnosticId:'fixture-diag'};
const tree=(id='one',dirty=false)=><ConfigProvider theme={{token:{motion:false}}}><App><InstanceSandboxSummary instanceId={id} revision={4} verifySetup dirty={dirty}/></App></ConfigProvider>;
beforeEach(()=>{vi.mocked(getInstanceSandbox).mockReset().mockResolvedValue(config);vi.mocked(verifyInstanceSandbox).mockReset().mockResolvedValue(evidence);});
it('verifies saved setup with exact revision and exposes collapsed failure evidence',async()=>{
 render(tree());await screen.findByText('Fixture · revision 2');fireEvent.click(screen.getByRole('button',{name:'Verify Instance setup'}));
 await waitFor(()=>expect(verifyInstanceSandbox).toHaveBeenCalledWith('one',4));
 const output=await screen.findByRole('button',{name:'Setup verification standard output'});expect(output).toHaveAttribute('aria-expanded','false');fireEvent.click(output);await screen.findByText('Hello, world!');
 expect(screen.getByText(/Exit 7/)).toBeInTheDocument();
});
it('requires saved configuration before verification',async()=>{
 render(tree('one',true));await screen.findByText('Fixture · revision 2');expect(screen.getByRole('button',{name:'Verify Instance setup'})).toBeDisabled();expect(verifyInstanceSandbox).not.toHaveBeenCalled();
});
it('ignores a late verification result after switching Instances',async()=>{
 let resolve!:(value:SandboxVerification)=>void;vi.mocked(verifyInstanceSandbox).mockImplementationOnce(()=>new Promise(done=>{resolve=done;}));
 const view=render(tree());await screen.findByText('Fixture · revision 2');fireEvent.click(screen.getByRole('button',{name:'Verify Instance setup'}));await waitFor(()=>expect(verifyInstanceSandbox).toHaveBeenCalled());
 view.rerender(tree('two'));await waitFor(()=>expect(getInstanceSandbox).toHaveBeenCalledWith('two',expect.any(AbortSignal)));
 await act(async()=>resolve(evidence));expect(screen.queryByText(evidence.message)).not.toBeInTheDocument();
});
