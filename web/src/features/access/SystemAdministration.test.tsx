import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { SystemAdministration } from './SystemAdministration';
import { resetIdentityCsrf } from '../../services/identity';
const account = { accountId:'account-reference',displayName:'Long platform Account fixture',loginKey:'FIXTURE@EXAMPLE.TEST',status:0,createdAt:'2026-10-21T00:00:00Z',version:'a'.repeat(64) };
const operator = {...account,isOperator:false,operatorVersion:'c'.repeat(64)};
const tenant = { tenantId:'organization-reference',name:'Organization fixture',slug:'organization-fixture',status:0,createdAt:account.createdAt,revision:7 };
const response = (value:unknown,status=200) => status===204 ? new Response(null,{status}) : new Response(JSON.stringify(value),{status,headers:{'Content-Type':'application/json'}});
const fetchMock=vi.fn<typeof fetch>(); let row:typeof account|typeof tenant|typeof operator; let total:number; let page:number; let put:(body:Record<string,unknown>)=>Response;
beforeEach(()=>{resetIdentityCsrf();fetchMock.mockReset();vi.stubGlobal('fetch',fetchMock);row={...account};total=1;page=0;put=()=>response(null,204);fetchMock.mockImplementation(async(url,options)=>{
  if(String(url).endsWith('/auth/csrf'))return response({token:'synthetic-csrf'});
  if(options?.method==='PUT')return put(JSON.parse(String(options.body)));
  return response({items:[row],total,page,pageSize:30});
});});
afterEach(()=>{cleanup();vi.restoreAllMocks();vi.unstubAllGlobals();});
const writes=()=>fetchMock.mock.calls.filter(([,o])=>o?.method==='PUT');
const mount=(kind:'accounts'|'tenants'|'operators'='accounts',permissions=['system.accounts.manage','system.tenants.manage'])=>render(<SystemAdministration kind={kind} permissions={permissions}/>);
async function inspect(name=account.displayName){fireEvent.click(await screen.findByRole('button',{name:'Inspect '+name}));return (await screen.findByText('Exact ID')).closest('[role=dialog]') as HTMLElement;}
async function choose(dialog:HTMLElement,status='Suspended',label='New status'){fireEvent.mouseDown(within(dialog).getByRole('combobox',{name:label}));await waitFor(()=>expect(document.querySelector('.ant-select-dropdown:not(.ant-select-dropdown-hidden)')).toBeTruthy());const dropdown=document.querySelector<HTMLElement>('.ant-select-dropdown:not(.ant-select-dropdown-hidden)')!;fireEvent.click(within(dropdown).getAllByText(status,{exact:true}).at(-1)!);}
async function review(dialog:HTMLElement,title='Change Account status?'){fireEvent.click(within(dialog).getByRole('button',{name:'Review status change'}));return (await screen.findAllByText(title))[0].closest('[role=dialog]') as HTMLElement;}

it('inspects only metadata and retains the draft after cancelling exact confirmation, with no write',async()=>{
  mount();const dialog=await inspect();expect(within(dialog).getByText(account.accountId)).toBeVisible();expect(within(dialog).getByRole('button',{name:'Review status change'})).toBeDisabled();
  await choose(dialog);const confirm=await review(dialog);await waitFor(()=>expect(within(confirm).getByText('Active → Suspended')).toBeVisible());expect(within(confirm).getByText(account.accountId)).toBeVisible();expect(writes()).toHaveLength(0);
  fireEvent.click(within(confirm).getByRole('button',{name:'Cancel'}));await waitFor(()=>expect(screen.queryByText('Change Account status?')).not.toBeInTheDocument());expect(within(dialog).getByRole('button',{name:'Review status change'})).toBeEnabled();expect(writes()).toHaveLength(0);
});
it('writes only the exact captured Account status/version after deliberate confirmation',async()=>{
  mount();const dialog=await inspect();await choose(dialog);const confirm=await review(dialog);fireEvent.click(within(confirm).getByRole('button',{name:'Confirm status change'}));await waitFor(()=>expect(writes()).toHaveLength(1));
  const [url,options]=writes()[0];expect(url).toBe('/api/v2/system/accounts/account-reference/status');expect(JSON.parse(String(options?.body))).toEqual({status:'Suspended',expectedVersion:account.version});await waitFor(()=>expect(screen.queryByText('Exact ID')).not.toBeInTheDocument());
});
it.each([409,503])('requires deliberate current readback after refused/unconfirmed %i, preserves choice and uses fresh witnesses',async status=>{
  put=()=>response({reasonCode:'stale_account'},status);mount();let dialog=await inspect();await choose(dialog);let confirm=await review(dialog);fireEvent.click(within(confirm).getByRole('button',{name:'Confirm status change'}));
  await screen.findByText('Read current status before saving again');expect(within(dialog).getByRole('button',{name:'Review status change'})).toBeDisabled();expect(writes()).toHaveLength(1);
  row={...account,version:'b'.repeat(64)};put=()=>response(null,204);fireEvent.click(within(dialog).getByRole('button',{name:'Reload current status'}));
  await waitFor(()=>expect(screen.getByRole('button',{name:'Review status change'})).toBeEnabled());dialog=(screen.getByText('Exact ID').closest('[role=dialog]')) as HTMLElement;
  expect(within(dialog).getByRole('combobox',{name:'New status'}).closest('.ant-select')).toHaveTextContent('Suspended');confirm=await review(dialog);fireEvent.click(within(confirm).getByRole('button',{name:'Confirm status change'}));await waitFor(()=>expect(writes()).toHaveLength(2));expect(JSON.parse(String(writes()[1][1]?.body)).expectedVersion).toBe('b'.repeat(64));
});
it('does not repeat an unknown change when readback already confirms the retained choice',async()=>{
  put=()=>response({reasonCode:'unknown_result'},503);mount();const dialog=await inspect();await choose(dialog);const confirm=await review(dialog);fireEvent.click(within(confirm).getByRole('button',{name:'Confirm status change'}));await screen.findByText('Read current status before saving again');
  row={...account,status:1,version:'b'.repeat(64)};fireEvent.click(within(dialog).getByRole('button',{name:'Reload current status'}));await waitFor(()=>expect(screen.queryByText('Read current status before saving again')).not.toBeInTheDocument());expect(screen.getByRole('button',{name:'Review status change'})).toBeDisabled();expect(writes()).toHaveLength(1);
});
it('keeps independently readable organization metadata read-only without platform management',async()=>{
  row={...tenant};mount('tenants',['system.tenants.read']);const dialog=await inspect(tenant.name);expect(within(dialog).getByRole('combobox',{name:'New status'})).toBeDisabled();expect(within(dialog).getByText(/requires platform management permission/)).toBeVisible();expect(screen.queryByRole('button',{name:'Create organization'})).not.toBeInTheDocument();expect(writes()).toHaveLength(0);
});
it('sends the exact organization revision and never offers a Removed organization command',async()=>{
  row={...tenant};mount('tenants');const dialog=await inspect(tenant.name);await choose(dialog);expect(document.querySelector('.ant-select-dropdown')).not.toHaveTextContent('Removed');const confirm=await review(dialog,'Change organization status?');fireEvent.click(within(confirm).getByRole('button',{name:'Confirm status change'}));await waitFor(()=>expect(writes()).toHaveLength(1));expect(writes()[0][0]).toBe('/api/v2/system/tenants/organization-reference/status');expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({status:'Suspended',expectedRevision:7});
});
it('applies draft filters only explicitly, pages on the server and retires selected details',async()=>{
  total=31;mount();await screen.findByText('31 Accounts');const before=fetchMock.mock.calls.length;fireEvent.change(screen.getByLabelText('Account name or email'),{target:{value:'Long'}});expect(fetchMock.mock.calls).toHaveLength(before);
  fireEvent.click(screen.getByRole('button',{name:'Apply filters'}));await waitFor(()=>expect(fetchMock.mock.calls.at(-1)?.[0]).toBe('/api/v2/system/accounts?page=0&pageSize=30&q=Long'));
  page=1;fireEvent.click(await screen.findByTitle('2',{exact:true}));await waitFor(()=>expect(fetchMock.mock.calls.at(-1)?.[0]).toBe('/api/v2/system/accounts?page=1&pageSize=30&q=Long'));
});
it('removes private metadata/actions after a denied current-status reload',async()=>{
  mount();const dialog=await inspect();fetchMock.mockImplementation(async()=>response({reasonCode:'permission_denied'},403));fireEvent.click(within(dialog).getByRole('button',{name:'Reload current status'}));await screen.findByRole('alert');await waitFor(()=>expect(screen.queryByText('Exact ID')).not.toBeInTheDocument());expect(screen.queryByText(account.displayName)).not.toBeInTheDocument();expect(screen.queryByText('1 Account')).not.toBeInTheDocument();
});

it('explains a final platform operator refusal without implying an uncertain or applied change',async()=>{
  put=()=>response({reasonCode:'last_system_operator'},409);mount();const dialog=await inspect();await choose(dialog);const confirm=await review(dialog);fireEvent.click(within(confirm).getByRole('button',{name:'Confirm status change'}));
  expect(await screen.findByText('Keep at least one active platform operator. This Account was not changed. Reload current status before choosing another action.')).toBeVisible();expect(screen.getByRole('button',{name:'Review status change'})).toBeDisabled();expect(writes()).toHaveLength(1);
});


it('reviews the exact built-in platform role with powers, target and login invalidation before writing',async()=>{
  row={...operator};mount('operators',['system.governance.manage']);const dialog=await inspect();expect(within(dialog).getByText('Current platform role')).toBeVisible();
  expect(within(dialog).getByRole('button',{name:'Review role change'})).toBeDisabled();await choose(dialog,'Platform Operator','New platform role');
  fireEvent.click(within(dialog).getByRole('button',{name:'Review role change'}));const confirmation=(await screen.findAllByText('Change platform operator role?'))[0].closest('[role=dialog]') as HTMLElement;
  await waitFor(()=>expect(within(confirmation).getByText('No platform role → Platform Operator')).toBeVisible());expect(within(confirmation).getByText(account.accountId)).toBeVisible();expect(within(confirmation).getByText(/All existing logins for this Account are invalidated/)).toBeVisible();expect(writes()).toHaveLength(0);
  fireEvent.click(within(confirmation).getByRole('button',{name:'Confirm role change'}));await waitFor(()=>expect(writes()).toHaveLength(1));expect(writes()[0][0]).toBe('/api/v2/system/operators/account-reference');expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({enabled:true,expectedVersion:operator.operatorVersion});
});
it.each([409,503])('retains the operator draft and prevents replay after %i until exact readback',async status=>{
  row={...operator};put=()=>response({reasonCode:'stale_operator'},status);mount('operators',['system.governance.manage']);let dialog=await inspect();await choose(dialog,'Platform Operator','New platform role');fireEvent.click(within(dialog).getByRole('button',{name:'Review role change'}));
  let confirmation=(await screen.findAllByText('Change platform operator role?'))[0].closest('[role=dialog]') as HTMLElement;fireEvent.click(within(confirmation).getByRole('button',{name:'Confirm role change'}));await screen.findByText('Read current platform role before saving again');expect(screen.getByRole('button',{name:'Review role change'})).toBeDisabled();
  row={...operator,operatorVersion:'d'.repeat(64)};put=()=>response(null,204);fireEvent.click(screen.getByRole('button',{name:'Reload current platform role'}));await waitFor(()=>expect(screen.getByRole('button',{name:'Review role change'})).toBeEnabled());
  dialog=screen.getByText('Exact ID').closest('[role=dialog]') as HTMLElement;fireEvent.click(within(dialog).getByRole('button',{name:'Review role change'}));confirmation=(await screen.findAllByText('Change platform operator role?'))[0].closest('[role=dialog]') as HTMLElement;fireEvent.click(within(confirmation).getByRole('button',{name:'Confirm role change'}));await waitFor(()=>expect(writes()).toHaveLength(2));expect(JSON.parse(String(writes()[1][1]?.body))).toEqual({enabled:true,expectedVersion:'d'.repeat(64)});
});
it('readback of an already-applied role disables replay after a lost reply',async()=>{
  row={...operator};put=()=>response({reasonCode:'lost_reply'},503);mount('operators',['system.governance.manage']);const dialog=await inspect();await choose(dialog,'Platform Operator','New platform role');fireEvent.click(within(dialog).getByRole('button',{name:'Review role change'}));
  const confirmation=(await screen.findAllByText('Change platform operator role?'))[0].closest('[role=dialog]') as HTMLElement;fireEvent.click(within(confirmation).getByRole('button',{name:'Confirm role change'}));await screen.findByText('Read current platform role before saving again');
  row={...operator,isOperator:true,operatorVersion:'d'.repeat(64)};fireEvent.click(screen.getByRole('button',{name:'Reload current platform role'}));await waitFor(()=>expect(screen.queryByText('Read current platform role before saving again')).not.toBeInTheDocument());expect(screen.getByRole('button',{name:'Review role change'})).toBeDisabled();expect(writes()).toHaveLength(1);
});
it('allows revocation on an inactive Account but refuses a new operator grant in the editor',async()=>{
  row={...operator,status:1};mount('operators',['system.governance.manage']);let dialog=await inspect();await choose(dialog,'Platform Operator','New platform role');expect(screen.getByRole('button',{name:'Review role change'})).toBeDisabled();expect(within(dialog).getByText(/An inactive Account cannot receive/)).toBeVisible();cleanup();
  row={...operator,status:1,isOperator:true};mount('operators',['system.governance.manage']);dialog=await inspect();await choose(dialog,'No platform role','New platform role');expect(screen.getByRole('button',{name:'Review role change'})).toBeEnabled();
});
it('keeps Account status and platform-role filters independent and applies the role only explicitly',async()=>{
  row={...operator};mount('operators',['system.governance.manage']);await screen.findByText('1 Account');const before=fetchMock.mock.calls.length;fireEvent.mouseDown(screen.getByLabelText('Platform role'));const dropdown=await waitFor(()=>{const d=document.querySelector<HTMLElement>('.ant-select-dropdown:not(.ant-select-dropdown-hidden)');expect(d).toBeTruthy();return d!;});fireEvent.click(within(dropdown).getAllByText('No platform role',{exact:true}).at(-1)!);expect(fetchMock.mock.calls).toHaveLength(before);fireEvent.click(screen.getByRole('button',{name:'Apply filters'}));await waitFor(()=>expect(fetchMock.mock.calls.at(-1)?.[0]).toBe('/api/v2/system/operators?page=0&pageSize=30&isOperator=false'));
});
it('rejects malformed operator metadata without exposing an editor',async()=>{
  row={...account};mount('operators',['system.governance.manage']);await screen.findByRole('alert');expect(screen.queryByRole('button',{name:'Inspect '+account.displayName})).not.toBeInTheDocument();
});
