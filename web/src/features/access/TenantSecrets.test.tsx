import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import { TenantSecrets } from './TenantSecrets';
const tenant='11111111-1111-4111-8111-111111111111';
const context={tenant:{tenantId:tenant,name:'Fixture organization'},permissions:['secrets.manage'],revision:10} as unknown as TenantContext;
const fetchMock=vi.fn();let badAcknowledgement=false;
const response=(value:unknown)=>new Response(JSON.stringify(value));
beforeEach(()=>{resetIdentityCsrf();badAcknowledgement=false;fetchMock.mockReset();vi.stubGlobal('fetch',fetchMock);fetchMock.mockImplementation(async(url:string,options?:RequestInit)=>{
  if(url.endsWith('/csrf'))return response({token:'fixture-csrf'});
  if(url.endsWith('/access/me'))return response({revision:10});
  if(options?.method==='POST'){const body=JSON.parse(String(options.body));return response({secretId:badAcknowledgement?'22222222-2222-4222-8222-222222222222':body.secretId,name:body.name,description:body.description,status:'Active',revision:1});}
  return response({items:[],total:0,page:0,pageSize:20});
});});
afterEach(()=>{resetIdentityCsrf();vi.unstubAllGlobals();});
async function createDraft(){fireEvent.click(await screen.findByRole('button',{name:'Create Secret'}));const drawer=await screen.findByRole('dialog',{name:'Create Secret'});fireEvent.change(within(drawer).getByRole('textbox',{name:/Name/}),{target:{value:'Protected fixture'}});fireEvent.change(within(drawer).getByLabelText('Protected value'),{target:{value:'WRITE_ONLY_FIXTURE_SENTINEL'}});return drawer;}
it('submits a stable caller-generated Secret identity and closes only after its exact acknowledgement',async()=>{
  render(<App><TenantSecrets context={context}/></App>);const drawer=await createDraft();
  await act(async()=>{fireEvent.click(within(drawer).getByRole('button',{name:'Save Secret'}));});
  await waitFor(()=>expect(screen.queryByRole('dialog',{name:'Create Secret'})).not.toBeInTheDocument());
  const writes=fetchMock.mock.calls.filter(([,options])=>options?.method==='POST');expect(writes).toHaveLength(1);
  const body=JSON.parse(String(writes[0][1].body));expect(body.secretId).toMatch(/^[a-f0-9-]{36}$/);expect(body).toMatchObject({name:'Protected fixture',protectedValue:'WRITE_ONLY_FIXTURE_SENTINEL',expectedRevision:10});
  expect(screen.queryByDisplayValue('WRITE_ONLY_FIXTURE_SENTINEL')).not.toBeInTheDocument();
});
it('treats an acknowledgement for a different Secret as uncertain and never resends the protected value',async()=>{
  badAcknowledgement=true;render(<App><TenantSecrets context={context}/></App>);const drawer=await createDraft();
  await act(async()=>{fireEvent.click(within(drawer).getByRole('button',{name:'Save Secret'}));});
  await within(drawer).findByText('Save outcome is unconfirmed');expect(within(drawer).getByRole('button',{name:'Save Secret'})).toBeDisabled();
  expect(fetchMock.mock.calls.filter(([,options])=>options?.method==='POST')).toHaveLength(1);
});
