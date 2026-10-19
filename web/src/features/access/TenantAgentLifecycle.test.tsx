import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { TenantAgentLifecycle } from './TenantAgentLifecycle';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import type { AgentLifecycle } from '../../services/tenantAgentLifecycle';
const context: TenantContext = { tenant: {tenantId:'tenant',name:'Stale organization label',slug:'lifecycle'},userId:'user',principalId:'owner',permissions:[],roles:[],revision:7 };
const initial = (): AgentLifecycle => ({instanceId:'agent',agentName:'Alex <safe>',organizationName:'Lifecycle organization',lifecycle:'Active',instanceRevision:2,securityRevision:7,decommissioned:false,canDecommission:false,canUpdate:true,blocker:'archive_required',operationId:null});
const fetchMock=vi.fn<typeof fetch>();let current:AgentLifecycle, failure:number|undefined, lost:boolean, cleanupStatus:string, readFailure:number|undefined;const writes=()=>fetchMock.mock.calls.filter(([,o])=>o?.method==='DELETE'||o?.method==='PUT');
const response=(body:unknown,status=200)=>new Response(JSON.stringify(body),{status,headers:{'Content-Type':'application/json'}});
beforeEach(()=>{resetIdentityCsrf();current=initial();failure=undefined;lost=false;cleanupStatus='completed';readFailure=undefined;fetchMock.mockReset();vi.stubGlobal('fetch',fetchMock);fetchMock.mockImplementation(async(path,options)=>{
  if(String(path).endsWith('/csrf'))return response({token:'csrf'});
  if(options?.method==='PUT'||options?.method==='DELETE'){
    if(failure)return response({reasonCode:failure===409?'stale_revision':'permission_denied'},failure);
    const body=JSON.parse(String(options.body));
    if(options.method==='DELETE'){current={...current,instanceRevision:current.instanceRevision+1,securityRevision:current.securityRevision+1,decommissioned:true,canUpdate:false,canDecommission:false,blocker:'agent_decommissioned',operationId:body.operationId};return lost?new Response('lost reply'):response({operationId:body.operationId,status:'decommissioned',cleanupStatus});}
    current={...current,lifecycle:body.lifecycle===1?'Archived':'Active',instanceRevision:current.instanceRevision+1,securityRevision:current.securityRevision+1,blocker:body.lifecycle===1?null:'archive_required',canDecommission:body.lifecycle===1};return response({instance:{instanceId:'agent',lifecycle:current.lifecycle,revision:current.instanceRevision}});
  }
  return readFailure?response({reasonCode:'permission_denied'},readFailure):response(current);
});});
afterEach(()=>{cleanup();vi.restoreAllMocks();vi.unstubAllGlobals();});
async function open(){render(<App><TenantAgentLifecycle context={context} id="agent" /></App>);await screen.findByRole('region',{name:'Agent lifecycle'});}
async function confirm(action:string){fireEvent.click(screen.getByRole('button',{name:action}));const modal=await screen.findByRole('dialog');fireEvent.click(within(modal).getByRole('button',{name:action}));await waitFor(()=>expect(screen.queryByRole('dialog')).not.toBeInTheDocument());}
it('archives and restores with exact current witnesses through the single lifecycle owner',async()=>{
  await open();await confirm('Archive instance');await screen.findByRole('button',{name:'Restore instance'});await waitFor(()=>expect(screen.getByRole('button',{name:'Restore instance'})).toBeEnabled());
  expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({expectedRevision:7,expectedInstanceRevision:2,lifecycle:1});
  await confirm('Restore instance');await waitFor(()=>expect(screen.getByRole('button',{name:'Archive instance'})).toBeEnabled());expect(writes()).toHaveLength(2);
});
it('names exact Agent and organization, cancel writes nothing, then records retirement separately from pending cleanup',async()=>{
  current={...current,lifecycle:'Archived',canDecommission:true,blocker:null};cleanupStatus='pending';await open();fireEvent.click(screen.getByRole('button',{name:'Decommission instance'}));let dialog=await screen.findByRole('dialog');expect(dialog).toHaveTextContent('Agent: Alex <safe>');expect(dialog).toHaveTextContent('Organization: Lifecycle organization');expect(dialog).toHaveTextContent('cannot be restored or reused');expect(writes()).toHaveLength(0);fireEvent.click(within(dialog).getByRole('button',{name:'Cancel'}));await waitFor(()=>expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  await confirm('Decommission instance');await screen.findByText('Instance decommissioned');expect(screen.getByText(/cleanup is pending and will be retried/)).toBeVisible();expect(screen.queryByRole('button',{name:'Restore instance'})).not.toBeInTheDocument();expect(writes()).toHaveLength(1);expect(JSON.parse(String(writes()[0][1]?.body))).toMatchObject({expectedRevision:7,expectedInstanceRevision:2,operationId:expect.any(String)});
});
it('retires a stale witness until deliberate readback and asks again for a new exact action',async()=>{
  current={...current,lifecycle:'Archived',canDecommission:true,blocker:null};failure=409;await open();await confirm('Decommission instance');await screen.findByText('Read current lifecycle before another action');expect(screen.getByRole('button',{name:'Decommission instance'})).toBeDisabled();expect(writes()).toHaveLength(1);
  current={...current,agentName:'Renamed current Agent',securityRevision:9,instanceRevision:3};failure=undefined;fireEvent.click(screen.getByRole('button',{name:'Reload lifecycle'}));await screen.findByText('Renamed current Agent');await confirm('Decommission instance');expect(writes()).toHaveLength(2);const before=JSON.parse(String(writes()[0][1]?.body)),after=JSON.parse(String(writes()[1][1]?.body));expect(after).toMatchObject({expectedRevision:9,expectedInstanceRevision:3});expect(after.operationId).not.toBe(before.operationId);
});
it('recovers a lost successful reply through GET without replay or claiming physical cleanup',async()=>{
  current={...current,lifecycle:'Archived',canDecommission:true,blocker:null};lost=true;await open();await confirm('Decommission instance');await screen.findByText('Read current lifecycle before another action');expect(writes()).toHaveLength(1);fireEvent.click(screen.getByRole('button',{name:'Reload lifecycle'}));await screen.findByText('Permanently retired identity');expect(screen.getByText(/does not confirm the current physical cleanup outcome/)).toBeVisible();expect(writes()).toHaveLength(1);
});
it.each(['agent_in_use','browser_profile_busy'] as const)('explains %s and cannot dispatch retirement',async blocker=>{
  current={...current,lifecycle:'Archived',canDecommission:false,blocker};await open();expect(screen.getByRole('button',{name:'Decommission instance'})).toBeDisabled();expect(screen.getByText('Retirement is blocked')).toBeVisible();expect(writes()).toHaveLength(0);
});
it('uses server scoped hints, so parent update does not imply delete',async()=>{
  current={...current,lifecycle:'Archived',canDecommission:false,blocker:null};await open();expect(screen.getByRole('button',{name:'Restore instance'})).toBeEnabled();expect(screen.getByRole('button',{name:'Decommission instance'})).toBeDisabled();expect(screen.getByText(/requires delete access as well as read access/)).toBeVisible();
});
it('clears protected names and controls after denied readback',async()=>{
  current={...current,lifecycle:'Archived',canDecommission:true,blocker:null};await open();readFailure=403;fireEvent.click(screen.getByRole('button',{name:'Reload lifecycle'}));await screen.findByText('Read current lifecycle before another action');expect(screen.queryByText('Alex <safe>')).not.toBeInTheDocument();expect(screen.queryByRole('region',{name:'Agent lifecycle'})).not.toBeInTheDocument();
});
it('retains a confirmed retirement when the automatic authorized read fails',async()=>{
  current={...current,lifecycle:'Archived',canDecommission:true,blocker:null};await open();readFailure=403;await confirm('Decommission instance');expect(await screen.findByText('Instance decommissioned')).toBeVisible();expect(screen.getByText(/cleanup attempt completed/)).toBeVisible();expect(screen.queryByText('Alex <safe>')).not.toBeInTheDocument();expect(writes()).toHaveLength(1);
});
it('rejects malformed read facts rather than offering commands',async()=>{
  current={...current,instanceRevision:NaN};render(<App><TenantAgentLifecycle context={context} id="agent" /></App>);await screen.findByText('Read current lifecycle before another action');expect(screen.queryByRole('button',{name:'Archive instance'})).not.toBeInTheDocument();
});
it('retires open confirmation when its mounted owner disappears',async()=>{
  await open();fireEvent.click(screen.getByRole('button',{name:'Archive instance'}));await screen.findByRole('dialog');cleanup();await act(async()=>{});expect(screen.queryByRole('dialog')).not.toBeInTheDocument();expect(writes()).toHaveLength(0);
});
it('keeps a confirmed archive acknowledgement when its automatic read fails',async()=>{
  await open();readFailure=403;await confirm('Archive instance');expect(await screen.findByText('Archive saved')).toBeVisible();expect(screen.queryByText('Alex <safe>')).not.toBeInTheDocument();expect(writes()).toHaveLength(1);
});
it('aborts an inflight owner command and suppresses a late successful completion after unmount',async()=>{
  let finish:(response:Response)=>void=()=>{};const original=fetchMock.getMockImplementation()!;fetchMock.mockImplementation((path,options)=>options?.method==='PUT'?new Promise(resolve=>{finish=resolve;}):original(path,options));
  const mounted=render(<App><TenantAgentLifecycle context={context} id="agent" /></App>);await screen.findByRole('region',{name:'Agent lifecycle'});fireEvent.click(screen.getByRole('button',{name:'Archive instance'}));const dialog=await screen.findByRole('dialog');fireEvent.click(within(dialog).getByRole('button',{name:'Archive instance'}));await waitFor(()=>expect(writes()).toHaveLength(1));const signal=writes()[0][1]?.signal as AbortSignal;const reads=fetchMock.mock.calls.length;mounted.unmount();expect(signal.aborted).toBe(true);
  await act(async()=>{finish(response({instance:{instanceId:'agent',lifecycle:'Archived',revision:3}}));});expect(fetchMock.mock.calls.length).toBe(reads);expect(screen.queryByText('Instance archived.')).not.toBeInTheDocument();
});
