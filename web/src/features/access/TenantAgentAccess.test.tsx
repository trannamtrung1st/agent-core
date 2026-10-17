import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { TenantAgentAccess, type AgentAccessView } from './TenantAgentAccess';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';

const context: TenantContext = { tenant: { tenantId: 'tenant', name: 'Access organization', slug: 'access' }, userId: 'user', principalId: 'owner', permissions: [], roles: [], revision: 7 };
const view: AgentAccessView = { instanceId: 'agent', principalId: 'executor', systemIdentityId: 'system-identity', name: 'Alex <safe>', lifecycle: 'Active', instanceRevision: 2, securityRevision: 7,
  canUpdate: false, canGrantChat: true, canGrantExecution: true, canInspectOrganization: true, authorityScope: 'agent',
  audience: { items: [], total: 0, page: 0, pageSize: 10 }, execution: { items: [], total: 0, page: 0, pageSize: 10 },
  principals: { items: [{ principalId: 'person', name: 'Avery', kind: 'user' }, { principalId: 'other-agent', name: 'Other agent', kind: 'agent' }], total: 2, page: 0, pageSize: 10 },
  chatRoles: [{ roleId: 'chat-role', key: 'chat', name: 'Chat member', isBuiltin: false, humanOnly: false, permissions: ['agents.chat'] }],
  executionRoles: [{ roleId: 'execution-role', key: 'execution', name: 'Credential reader', isBuiltin: false, humanOnly: false, permissions: ['credentials.read'] }],
  permissions: [{ key: 'agents.read', humanOnly: false, allowed: false, reasonCode: 'permission_denied', assignmentIds: [] }, { key: 'iam.roles.assign', humanOnly: true, allowed: false, reasonCode: 'human_only', assignmentIds: [] }] };
const fetchMock = vi.fn<typeof fetch>();
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
function renderView(data = view) { return render(<App><TenantAgentAccess context={context} id={data.instanceId} /></App>); }
async function select(name: string, label: string) { fireEvent.mouseDown(screen.getByRole('combobox', { name })); fireEvent.click(await screen.findByText(label, { selector: '.ant-select-item-option-content' })); }
beforeEach(() => { resetIdentityCsrf(); fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock); fetchMock.mockImplementation(async path => String(path).includes('/csrf') ? response({ token: 'csrf' }) : response(view)); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('keeps chat and execution separate and sends a source-bound chat assignment only after confirmation', async () => {
  renderView(); await screen.findByText('Alex <safe>');
  expect(screen.getByRole('region', { name: 'Who can chat' })).toBeVisible(); expect(screen.getByRole('region', { name: 'Agent execution authority' })).toBeVisible();
  expect(screen.queryByText('executor', { exact: true })).not.toBeInTheDocument();
  await select('Person or agent', 'Avery · Person'); await select('Chat role', 'Chat member');
  fireEvent.click(screen.getByRole('button', { name: 'Grant chat access' }));
  const dialog = await screen.findByRole('dialog'); await waitFor(() => expect(within(dialog).getByText(/Access organization/)).toBeVisible());
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(0);
  fireEvent.click(within(dialog).getByRole('button', { name: 'Grant chat access' }));
  await waitFor(() => expect(fetchMock.mock.calls.some(([, options]) => options?.method === 'POST')).toBe(true));
  const call = fetchMock.mock.calls.find(([, options]) => options?.method === 'POST')!;
  expect(call[0]).toBe('/api/v2/tenants/tenant/role-assignments');
  expect(JSON.parse(String(call[1]?.body))).toEqual({ principalId: 'person', roleId: 'chat-role', expectedRevision: 7, resourceType: 'agent', resourceId: 'agent' });
});

it('execution assignment targets the independent agent at organization scope', async () => {
  renderView(); await screen.findByText('Alex <safe>'); await select('Execution role', 'Credential reader'); fireEvent.click(screen.getByRole('button', { name: 'Grant execution role' }));
  const dialog = await screen.findByRole('dialog'); await waitFor(() => expect(within(dialog).getByText(/throughout this organization/)).toBeVisible());
  fireEvent.click(within(dialog).getByRole('button', { name: 'Grant execution role' }));
  await waitFor(() => expect(fetchMock.mock.calls.some(([, options]) => options?.method === 'POST')).toBe(true));
  expect(JSON.parse(String(fetchMock.mock.calls.find(([, options]) => options?.method === 'POST')![1]?.body))).toEqual({ principalId: 'executor', roleId: 'execution-role', expectedRevision: 7 });
});

it('retains a failed mutation selection and never silently retries a stale assignment', async () => {
  fetchMock.mockImplementation(async (path, options) => String(path).includes('/csrf') ? response({ token: 'csrf' }) : options?.method === 'POST' ? response({ reasonCode: 'stale_revision' }, 409) : response(view));
  renderView(); await screen.findByText('Alex <safe>'); await select('Person or agent', 'Avery · Person'); await select('Chat role', 'Chat member');
  fireEvent.click(screen.getByRole('button', { name: 'Grant chat access' })); const dialog = await screen.findByRole('dialog'); fireEvent.click(within(dialog).getByRole('button', { name: 'Grant chat access' }));
  expect(await screen.findByText(/Access has changed since you opened/)).toBeVisible(); expect(within(screen.getByRole('region', { name: 'Who can chat' })).getByText('Avery · Person', { exact: true })).toBeVisible();
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(1);
});

it('scoped readers have no directory or editing controls and cannot select organization authority', async () => {
  const read = { ...view, canGrantChat: false, canGrantExecution: false, canInspectOrganization: false, principals: { ...view.principals, items: [] } };
  fetchMock.mockImplementation(async () => response(read)); renderView(read); await screen.findByText('Alex <safe>');
  for (const name of ['Grant chat access', 'Grant execution role', 'Archive instance']) expect(screen.queryByRole('button', { name })).not.toBeInTheDocument();
  expect(screen.queryByRole('combobox', { name: 'Person or agent' })).not.toBeInTheDocument(); expect(screen.queryByRole('radio', { name: /^Organization$/ })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Agents' })); expect(await screen.findByText('Not granted', { exact: true })).toBeVisible();
});
